using System.Text.Json;

namespace KeiBuild.Catalog;

public sealed class CatalogStore
{
    public const int MaxImportCharacters = CatalogImport.MaxCharacters;

    private readonly object gate = new();
    private readonly CatalogSeed seed;
    private readonly CatalogImport import;
    private readonly string? reviewPath;
    private List<ReviewItem> reviews;

    public CatalogStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        seed = ReadFile<CatalogSeed>(Path.Combine(environment.ContentRootPath, "Data", "catalog.json"));

        import = new CatalogImport(seed.Chassis.Select(chassis => chassis.Code));

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
                .Select(item => item.Entry)).DistinctBy(CatalogImport.ClaimKey).ToArray();
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
        var candidates = import.Parse(payload);
        lock (gate)
        {
            var seen = seed.Entries.Concat(reviews.Select(item => item.Entry))
                .Select(CatalogImport.ClaimKey).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            var added = candidates.Where(entry => seen.Add(CatalogImport.ClaimKey(entry)))
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

    private void EnsureReviewEnabled()
    {
        if (reviewPath is null)
            throw new InvalidOperationException("The review inbox is available in Development.");
    }

    private static T ReadFile<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), CatalogImport.JsonOptions)
        ?? throw new InvalidDataException($"No catalog data found in {path}.");

    private void Save(List<ReviewItem> updated)
    {
        var path = reviewPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(updated, CatalogImport.JsonOptions));
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
