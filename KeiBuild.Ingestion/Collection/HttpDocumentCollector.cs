using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Ingestion.Collection;

public sealed class HttpDocumentCollector(HttpClient client, string stateDirectory)
{
    private static readonly TimeSpan OriginCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = ConnectPublicAsync,
        AutomaticDecompression = DecompressionMethods.All
    });

    public async Task<CollectedDocument> CollectAsync(SourceDefinition source, CancellationToken ct)
    {
        var uri = source.Validate();
        if (!source.AutomatedRetrievalApproved)
            throw new IngestionException("Automated retrieval is not approved in the source registry. Use a permitted local file.");
        ValidatePublicUrl(uri);

        using var rateFile = LockOrigin(uri.IdnHost);
        var cooldown = OriginCooldown;
        RecordNextAllowed(rateFile, DateTimeOffset.UtcNow + cooldown);
        try
        {
            var robotsUrl = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/robots.txt");
            var robots = await DownloadAsync(robotsUrl, format: null, ct);
            var delay = robots is null ? TimeSpan.FromSeconds(1) :
                RobotsRules.Check(DocumentBytes.Decode(robots.Bytes, robots.Charset), uri);
            if (delay > cooldown)
                cooldown = delay;
            await Task.Delay(delay, ct);
            return (await DownloadAsync(uri, source.Format, ct))!;
        }
        finally
        {
            RecordNextAllowed(rateFile, DateTimeOffset.UtcNow + cooldown);
        }
    }

    private async Task<CollectedDocument?> DownloadAsync(Uri url, DocumentFormat? format, CancellationToken ct)
    {
        var resource = format is null ? "robots.txt" : "source document";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd($"{RobotsRules.ProductToken}/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (format is null && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return null;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new IngestionException($"The {resource} returned HTTP {(int)response.StatusCode}. Use a permitted local document or update the approved URL after checking the source.");

            var header = response.Content.Headers.ContentType;
            if (!MatchesFormat(header?.MediaType?.ToLowerInvariant(), format))
                throw new IngestionException($"The {resource} returned an unexpected content type.");
            if (response.Content.Headers.ContentLength > DocumentBytes.MaxBytes)
                throw DocumentBytes.SizeError();

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await DocumentBytes.ReadAsync(stream, deadline.Token);
            return new CollectedDocument(bytes, DateTimeOffset.UtcNow, header?.CharSet, RetrievedFromUrl: true);
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new IngestionException($"Timed out retrieving the {resource}.", error);
        }
        catch (HttpRequestException error)
        {
            throw new IngestionException($"Could not retrieve the {resource} over public HTTPS. Check the approved URL and network.", error);
        }
    }

    private static bool MatchesFormat(string? mediaType, DocumentFormat? format) => format switch
    {
        null or DocumentFormat.Text => mediaType == "text/plain",
        DocumentFormat.Html => mediaType is "text/html" or "application/xhtml+xml",
        DocumentFormat.Markdown => mediaType is "text/markdown" or "text/plain",
        _ => false
    };

    private static void ValidatePublicUrl(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.');
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.Fragment.Length > 0 ||
            uri.HostNameType != UriHostNameType.Dns || !host.Contains('.') ||
            new[] { ".local", ".localhost", ".internal", ".test", ".example", ".invalid", ".onion" }
                .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            throw new IngestionException("Automated retrieval requires an approved public HTTPS URL on port 443 without a fragment.");
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        // Resolve once and connect to the validated IP so DNS cannot change the destination between checks.
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);
        foreach (var address in addresses.Where(IsPublicIPv4))
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("No reachable public IPv4 address for the approved source.");
    }

    private static bool IsPublicIPv4(IPAddress address) => address.GetAddressBytes() switch
    {
        [0 or 10 or 127 or >= 224, _, _, _] => false,
        [100, >= 64 and <= 127, _, _] => false,
        [169, 254, _, _] or [172, >= 16 and <= 31, _, _] => false,
        [192, 0, 0 or 2, _] or [192, 88, 99, _] or [192, 168, _, _] => false,
        [198, 18 or 19, _, _] or [198, 51, 100, _] or [203, 0, 113, _] => false,
        [_, _, _, _] => true,
        _ => false
    };

    private FileStream LockOrigin(string host)
    {
        Directory.CreateDirectory(stateDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.TrimEnd('.').ToLowerInvariant())));
        var file = Path.Combine(stateDirectory, key + ".txt");
        FileStream stream;
        try
        {
            stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IngestionException("Could not lock the origin's retrieval state. Another collection may be running.", error);
        }

        try
        {
            if (stream.Length > 0)
            {
                if (stream.Length > 80)
                    throw new IngestionException("The origin's retrieval state is invalid.");
                using var reader = new StreamReader(stream, leaveOpen: true);
                if (!DateTimeOffset.TryParseExact(reader.ReadToEnd(), "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var nextAllowed))
                    throw new IngestionException("The origin's retrieval state is invalid.");
                if (DateTimeOffset.UtcNow < nextAllowed)
                    throw new IngestionException($"The origin is in its retrieval cooldown. Retry after {nextAllowed:O}.");
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void RecordNextAllowed(FileStream stream, DateTimeOffset time)
    {
        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(Encoding.ASCII.GetBytes(time.ToString("O", CultureInfo.InvariantCulture)));
        stream.Flush(flushToDisk: true);
    }
}
