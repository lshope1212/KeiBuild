using System.Text;
using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Ingestion.Collection;

public sealed record CollectedDocument(byte[] Bytes, DateTimeOffset CollectedAtUtc, string? Charset = null,
    bool RetrievedFromUrl = false);

public static class DocumentBytes
{
    public const int MaxBytes = 512 * 1024;

    public static async Task<byte[]> ReadFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new IngestionException($"File not found or unreadable: {path}");

        await using var stream = info.OpenRead();
        if (stream.Length > MaxBytes)
            throw SizeError();
        return await ReadAsync(stream, ct);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (result.Length + read > MaxBytes)
                throw SizeError();
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    public static string Decode(byte[] bytes, string? charset)
    {
        try
        {
            // Legacy HTTP pages can declare a code page. Local text defaults to strict UTF-8 with BOM detection.
            var encoding = string.IsNullOrWhiteSpace(charset)
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                : Encoding.GetEncoding(charset.Trim('"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new IngestionException("Unable to decode the document. Save local documents in UTF-8 or use a source with a supported declared charset.", error);
        }
    }

    public static async Task<CollectedDocument> CollectFileAsync(string path, SourceDefinition source, CancellationToken ct)
    {
        source.Validate();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var matches = (source.Format, extension) switch
        {
            (DocumentFormat.Html, ".html" or ".htm") => true,
            (DocumentFormat.Markdown, ".md" or ".markdown") => true,
            (DocumentFormat.Text, ".txt") => true,
            _ => false
        };
        if (!matches)
            throw new IngestionException("Use a .html, .htm, .md, .markdown, or .txt file matching the format in the registry.");

        return new CollectedDocument(await ReadFileAsync(path, ct), DateTimeOffset.UtcNow);
    }

    public static IngestionException SizeError() => new("The document exceeds the 512 KiB size limit.");
}
