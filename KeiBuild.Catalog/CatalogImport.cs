using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeiBuild.Catalog;

public sealed class CatalogImport(IEnumerable<string> chassisCodes)
{
    public const int MaxCharacters = 40_000;
    public const int MaxEntries = 50;
    public const int MaxEvidenceLength = 500;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    private static readonly JsonSerializerOptions ExportOptions = new(JsonOptions)
    {
        // Keep Japanese text readable and within the inbox limit. This JSON is saved as a file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Dictionary<string, string> codes =
        chassisCodes.ToDictionary(code => code, StringComparer.OrdinalIgnoreCase);

    public FitmentEntry[] Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaxCharacters)
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

        if (entries is null || entries.Length is < 1 or > MaxEntries)
            throw new CatalogImportException("Include between 1 and 50 entries in a JSON array.");

        return entries.Select((entry, index) => Validate(entry, index + 1)).ToArray();
    }

    public string Serialize(IReadOnlyList<FitmentEntry> entries)
    {
        var payload = JsonSerializer.Serialize(entries, ExportOptions);
        // An empty extraction has no rows to submit to the inbox.
        if (payload.Length > MaxCharacters)
            throw new CatalogImportException("The export exceeds the review inbox's 40,000-character limit. Rerun with a smaller excerpt.");
        if (entries.Count > 0)
            Parse(payload);
        return payload;
    }

    public FitmentEntry Validate(FitmentEntry? entry, int row)
    {
        if (entry?.ChassisCode is null || !codes.TryGetValue(entry.ChassisCode.Trim(), out var code))
            throw new CatalogImportException($"Entry {row} needs a chassis code from the catalog.");

        var source = entry.Source;
        if (!Enum.IsDefined(entry.Kind) || !IsText(entry.Title, 100) || !IsText(entry.Manufacturer, 80) ||
            !IsText(entry.Category, 60) || !IsText(entry.Conditions, MaxEvidenceLength) ||
            (entry.Kind == EntryKind.Part && !IsText(entry.PartNumber, 80)) ||
            entry.PartNumber?.Length > 80 || entry.OemReference?.Length > 80)
        {
            throw new CatalogImportException($"Entry {row} needs a valid type, title, manufacturer, category, and fitment conditions. Parts also need a part number.");
        }

        if (source is null || !Enum.IsDefined(source.Kind) || !IsText(source.Title, 150) ||
            !IsText(source.Locator, 150) || !IsText(source.Evidence, MaxEvidenceLength) ||
            !TrySourceUrl(source.Url, out var url))
        {
            throw new CatalogImportException($"Entry {row} needs a source title, an HTTP(S) URL, a source type, a locator, and evidence.");
        }

        return entry with
        {
            ChassisCode = code,
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

    public static bool TrySourceUrl(string? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? url)
    {
        url = null;
        return IsText(value, 2048) && Uri.TryCreate(value!.Trim(), UriKind.Absolute, out url) &&
               url.Scheme is "https" or "http" && url.Host.Length > 0 && url.UserInfo.Length == 0;
    }

    public static (string, EntryKind, string, string, string, string, string) ClaimKey(FitmentEntry entry) =>
        (entry.ChassisCode, entry.Kind, Normalize(entry.Manufacturer), Normalize(entry.PartNumber ?? entry.Title),
         Normalize(entry.OemReference ?? ""), Normalize(entry.Conditions), new Uri(entry.Source.Url).AbsoluteUri);

    private static bool IsText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}

public sealed class CatalogImportException(string message) : Exception(message);
