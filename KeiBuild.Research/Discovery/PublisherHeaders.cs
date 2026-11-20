using System.Text.RegularExpressions;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Research.Discovery;

internal static partial class PublisherHeaders
{
    internal const string Missing = "Review: publisher permission for automated retrieval, AI processing and evidence retention is undocumented.";
    internal const string Basis = "The publisher returned TDM-Reservation: 0 and licensed this exact response under https://creativecommons.org/publicdomain/zero/1.0/. Confirm both signals on retrieval.";

    internal static void CheckRestrictions(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("TDM-Reservation", out var reservations) && !reservations.SequenceEqual(["0"]) ||
            response.Headers.TryGetValues("X-Robots-Tag", out var tags) && tags.SelectMany(t => t.ToLowerInvariant().Split([',', ' ']))
                .Any(t => t is "noai" or "none" or "noarchive" or "nosnippet"))
            throw new IngestionException("Publisher headers restrict processing or evidence retention.") { RequiresSourceReview = true };
    }

    internal static DocumentFormat? OpenFormat(HttpResponseMessage response)
    {
        CheckRestrictions(response);
        if (!response.Headers.TryGetValues("TDM-Reservation", out var tdm) || !tdm.SequenceEqual(["0"]) ||
            response.Headers.Contains("TDM-Policy") || !response.Headers.TryGetValues("Link", out var links) ||
            links.ToArray() is not [var link] || !LicenseLink().IsMatch(link)) return null;
        return response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
        {
            "text/html" or "application/xhtml+xml" => DocumentFormat.Html,
            "text/plain" => DocumentFormat.Text,
            "text/markdown" => DocumentFormat.Markdown,
            _ => null
        };
    }

    // Only an unambiguous license for this response. Links with anchors or additional parameters need review.
    [GeneratedRegex("""\A\s*<https://creativecommons\.org/publicdomain/zero/1\.0/>\s*;\s*(?i:rel)\s*=\s*(?:"license"|license)\s*\z""")]
    private static partial Regex LicenseLink();
}

