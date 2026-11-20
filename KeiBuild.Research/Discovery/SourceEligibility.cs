using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;
using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Sources;
using KeiBuild.Research.Data;

namespace KeiBuild.Research.Discovery;

internal static class SourceEligibility
{
    internal const string WikiTerms = "Wikipedia contributors. Article text under CC BY-SA 4.0. Attribute the article URL and license, identify adaptations and preserve share-alike. https://creativecommons.org/licenses/by-sa/4.0/ and https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use (section 7). API access: https://foundation.wikimedia.org/wiki/Policy:Wikimedia_Foundation_API_Usage_Guidelines";
    internal static string Key(string scope) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32];

    internal static bool IsArticle(Uri uri)
    {
        if (uri.IdnHost is not ("en.wikipedia.org" or "ja.wikipedia.org") ||
            !uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal) || uri.Query.Length != 0) return false;
        var title = Uri.UnescapeDataString(uri.AbsolutePath[6..]);
        return title.Length > 0 && title.IndexOfAny([':', '/', '#', '?', '%', '\\']) < 0 && !title.Any(char.IsControl);
    }

    internal static bool Unsupported(string url) => Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant() is
        ".pdf" or ".zip" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".xml" or ".json" or ".jpg" or ".png" or ".mp4";

    internal static bool HasVehicleContext(string text) =>
        new[] { "acty", "honda", "vehicle", "truck", "automotive", "chassis", "アクティ", "ホンダ", "自動車", "車台", "トラック" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    internal static SourcePolicy? Assess(DiscoveredResource item, List<SourcePolicy> policies, ResearchDb db)
    {
        var match = PolicyRules.Match(policies, item.Url);
        if (item.Publisher != null && item.Snippet.StartsWith("DOI ", StringComparison.Ordinal) &&
            match == null && !HasVehicleContext(item.Title))
        {
            item.Kind = ResourceKind.Irrelevant;
            item.Relevance = 0;
        }
        if (item.AccessHold != null || item.Kind == ResourceKind.Irrelevant)
        {
            item.Assessment = item.Kind == ResourceKind.Irrelevant ? "Irrelevant to the target." : item.AccessHold;
            return null;
        }
        if (item.Kind != ResourceKind.Sitemap && Unsupported(item.Url))
        {
            item.Assessment = "Review: unsupported source format.";
            return null;
        }
        var uri = new Uri(item.Url);
        if (match == null && IsArticle(uri))
        {
            var language = uri.IdnHost[..2];
            match = new SourcePolicy { CampaignId = item.CampaignId };
            PolicyRules.Apply(new("auto-wiki-" + Key(item.Url), item.Url, false, $"{language}.wikipedia.org contributors",
                SourceKind.Community, DocumentFormat.Html, WikiTerms, true, true, true, false, true, Language: language), match);
            policies.Add(match);
            db.Policies.Add(match);
        }
        var permitted = PolicyRules.Permitted(policies, item);
        item.Assessment = permitted != null ? $"Permitted under {permitted.Key}. {permitted.PermissionNotes}" :
            match != null ? $"Blocked by {match.Key}. {match.PermissionNotes}" :
            item.Assessment?.StartsWith("Metadata review:", StringComparison.Ordinal) == true ? item.Assessment : PublisherHeaders.Missing;
        return permitted;
    }

    private static IngestionException Restriction(string reason) => new(reason) { RequiresSourceReview = true };

    internal static CollectedDocument CheckDocument(CollectedDocument document, SourcePolicy policy, Uri uri)
    {
        if (policy.Format != DocumentFormat.Html) return document;
        var html = new HtmlParser().ParseDocument(DocumentBytes.Decode(document.Bytes, document.Charset));
        foreach (var meta in html.QuerySelectorAll("meta[name]"))
        {
            var name = meta.GetAttribute("name")?.ToLowerInvariant();
            var value = meta.GetAttribute("content")?.ToLowerInvariant() ?? "";
            if ((name is "robots" or "keibuildingestion" or "tdm-reservation") &&
                (value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Any(s => s is "none" or "noai" or "noarchive" or "nosnippet") ||
                 name == "tdm-reservation" && value.Trim() != "0"))
                throw Restriction("Page metadata restricts automated processing or evidence retention.");
        }
        if (html.QuerySelector("input[type=password], .g-recaptcha, .h-captcha, #challenge-form, [itemprop=isAccessibleForFree][content=false]") != null)
            throw Restriction("Page contains an access challenge or restricted content.");
        if (policy.Key.StartsWith("auto-header-", StringComparison.Ordinal) && html.QuerySelectorAll("[rel~=license]")
            .Any(link => link.GetAttribute("href") != "https://creativecommons.org/publicdomain/zero/1.0/"))
            throw Restriction("The HTML license conflicts with the publisher's HTTP license.");
        if (!IsArticle(uri)) return document;
        var license = html.QuerySelector("head link[rel~=license]")?.GetAttribute("href");
        var body = html.QuerySelector("#mw-content-text .mw-parser-output");
        if (!Uri.TryCreate(license, UriKind.Absolute, out var rights) || rights.Scheme != "https" ||
            rights.Host != "creativecommons.org" || !rights.AbsolutePath.StartsWith("/licenses/by-sa/4.0/", StringComparison.Ordinal) ||
            body == null || body.QuerySelector(".nonfree, .non-free, .copyrightvio, .mw-copyrightwarning") != null)
            throw Restriction("Article reuse conditions or article text could not be confirmed.");
        foreach (var element in body.QuerySelectorAll("figure, img, audio, video, blockquote, q, .quotebox, .reference, .reflist, .navbox, .sidebar, .mw-editsection, script, style, footer"))
            element.Remove();
        // Keep only article text and tables. Hash the original response separately in CampaignRunner.
        return document with { Bytes = Encoding.UTF8.GetBytes(body.OuterHtml), Charset = "utf-8" };
    }
}

