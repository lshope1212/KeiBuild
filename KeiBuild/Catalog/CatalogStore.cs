using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeiBuild.Catalog;

public sealed class CatalogStore
{
    public const int MaxImportCharacters = 40_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    private readonly object gate = new();
    private readonly CatalogSeed seed;
    private readonly string? reviewPath;
    private List<ReviewItem> reviews;

    public CatalogStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        seed = ReadFile<CatalogSeed>(Path.Combine(environment.ContentRootPath, "Data", "catalog.json"));

        if (environment.IsDevelopment())
        {
            var path = configuration["KeiBuild:ReviewFile"] ?? ".local/review.json";
            reviewPath = Path.GetFullPath(path, environment.ContentRootPath);
        }

        reviews = reviewPath is not null && File.Exists(reviewPath)
            ? ReadFile<List<ReviewItem>>(reviewPath)
            : [];
    }

    public IReadOnlyList<Chassis> Chassis => seed.Chassis;

    public Chassis? FindChassis(string? code) => Chassis.FirstOrDefault(chassis =>
        string.Equals(chassis.Code, code, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<FitmentEntry> PublishedEntries()
    {
        lock (gate)
        {
            return seed.Entries.Concat(reviews
                .Where(item => item.Status == ReviewStatus.Approved)
                .Select(item => item.Entry)).DistinctBy(ClaimKey).ToArray();
        }
    }

    public IReadOnlyList<ReviewItem> ReviewItems()
    {
        lock (gate)
        {
            return reviews.ToArray();
        }
    }

    public (int Added, int Duplicates) Import(string? payload)
    {
        EnsureReviewEnabled();
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaxImportCharacters)
            throw new CatalogImportException("Paste a JSON batch of up to 40,000 characters.");

        FitmentEntry?[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<FitmentEntry?[]>(payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new CatalogImportException($"Invalid JSON or unsupported fields at {exception.Path ?? "$"}. Check the import example.");
        }

        if (entries is null || entries.Length is < 1 or > 50)
            throw new CatalogImportException("Include between 1 and 50 entries in a JSON array.");

        // Validate the entire batch before writing any candidates.
        var candidates = entries.Select((entry, index) => Validate(entry, index + 1)).ToArray();
        lock (gate)
        {
            var seen = seed.Entries.Concat(reviews.Select(item => item.Entry))
                .Select(ClaimKey).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            var added = candidates.Where(entry => seen.Add(ClaimKey(entry)))
                .Select(entry => new ReviewItem(Guid.NewGuid(), entry, now, ReviewStatus.Pending))
                .ToArray();

            if (added.Length > 0)
                Save([.. reviews, .. added]);

            return (added.Length, candidates.Length - added.Length);
        }
    }

    public bool Review(Guid id, ReviewStatus status)
    {
        EnsureReviewEnabled();
        if (status is not (ReviewStatus.Approved or ReviewStatus.Rejected))
            throw new ArgumentOutOfRangeException(nameof(status));

        lock (gate)
        {
            var index = reviews.FindIndex(item => item.Id == id && item.Status == ReviewStatus.Pending);
            if (index < 0)
                return false;

            List<ReviewItem> updated = [.. reviews];
            updated[index] = updated[index] with { Status = status, ReviewedAt = DateTimeOffset.UtcNow };
            Save(updated);
            return true;
        }
    }

    private FitmentEntry Validate(FitmentEntry? entry, int row)
    {
        var chassis = FindChassis(entry?.ChassisCode?.Trim());
        if (entry is null || chassis is null)
            throw new CatalogImportException($"Entry {row} needs a chassis code from the catalog.");

        var source = entry.Source;
        if (!IsText(entry.Title, 100) || !IsText(entry.Manufacturer, 80) ||
            !IsText(entry.Category, 60) || !IsText(entry.Conditions, 500) ||
            (entry.Kind == EntryKind.Part && !IsText(entry.PartNumber, 80)) ||
            entry.PartNumber?.Length > 80 || entry.OemReference?.Length > 80)
        {
            throw new CatalogImportException($"Entry {row} needs a title, manufacturer, category, and fitment conditions. Parts also need a part number.");
        }

        if (source is null || !IsText(source.Title, 150) || !IsText(source.Locator, 150) ||
            !IsText(source.Evidence, 500) || !IsText(source.Url, 2048) ||
            !Uri.TryCreate(source.Url.Trim(), UriKind.Absolute, out var url) ||
            url.Scheme is not ("https" or "http") || url.Host.Length == 0 || url.UserInfo.Length != 0)
        {
            throw new CatalogImportException($"Entry {row} needs a source title, an HTTP(S) URL, a locator, and evidence.");
        }

        return entry with
        {
            ChassisCode = chassis.Code,
            Title = entry.Title.Trim(),
            Manufacturer = entry.Manufacturer.Trim(),
            Category = entry.Category.Trim(),
            Conditions = entry.Conditions.Trim(),
            PartNumber = NullIfBlank(entry.PartNumber),
            OemReference = NullIfBlank(entry.OemReference),
            Source = source with
            {
                Title = source.Title.Trim(), Url = url.AbsoluteUri,
                Locator = source.Locator.Trim(), Evidence = source.Evidence.Trim()
            }
        };
    }

    private static bool IsText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (string, EntryKind, string, string, string, string, string) ClaimKey(FitmentEntry entry) =>
        (entry.ChassisCode, entry.Kind, Normalize(entry.Manufacturer), Normalize(entry.PartNumber ?? entry.Title),
         Normalize(entry.OemReference ?? ""), Normalize(entry.Conditions), new Uri(entry.Source.Url).AbsoluteUri);

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private void EnsureReviewEnabled()
    {
        if (reviewPath is null)
            throw new InvalidOperationException("The review inbox is available in Development.");
    }

    private static T ReadFile<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"No catalog data found in {path}.");

    private void Save(List<ReviewItem> updated)
    {
        var path = reviewPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(updated, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            reviews = updated;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record CatalogSeed(Chassis[] Chassis, FitmentEntry[] Entries);
}

public sealed class CatalogImportException(string message) : Exception(message);
