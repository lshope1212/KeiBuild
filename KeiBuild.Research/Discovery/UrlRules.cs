using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Sources;
using KeiBuild.Research.Data;

namespace KeiBuild.Research.Discovery;

public static class UrlRules
{
    public static Uri? Normalize(string? value, Uri? baseUri = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\'))
            return null;
        if (!(baseUri is null ? Uri.TryCreate(value, UriKind.Absolute, out var uri) : Uri.TryCreate(baseUri, value, out uri)) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns)
            return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!host.Contains('.') || uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("%25", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains(';'))
            return null;
        var canonical = new UriBuilder(uri) { Scheme = "https", Host = host, Port = -1, Fragment = "" }.Uri;
        return canonical.AbsoluteUri.Length <= 2048 ? canonical : null;
    }
}

public sealed record PolicyInput(string Key, string Scope, bool Prefix, string Publisher, SourceKind Kind,
    DocumentFormat Format, string PermissionNotes, bool AllowFetch, bool AllowAi, bool AllowEvidence,
    bool AllowSnapshot, bool ExploreLinks, int DelaySeconds = 10, string? Language = null, string? SitemapUrl = null);

public static class PolicyRules
{
    public static void Apply(PolicyInput input, SourcePolicy policy)
    {
        var uri = UrlRules.Normalize(input.Scope);
        if (uri is null || input.Scope.Contains('#') || string.IsNullOrWhiteSpace(input.Key) || input.Key.Length > 80 ||
            (input.Prefix && (uri.Query.Length > 0 || !uri.AbsolutePath.EndsWith('/'))) ||
            input.DelaySeconds is < 10 or > 300 || string.IsNullOrWhiteSpace(input.Publisher) || input.Publisher.Contains('\0') ||
            string.IsNullOrWhiteSpace(input.PermissionNotes) || input.PermissionNotes.Contains('\0') ||
            input.Language?.Contains('\0') == true || input.Key.Contains('\0'))
            throw new IngestionException("Each source policy needs a key and a safe HTTPS URL or a slash-terminated path prefix, plus a 10–300 second delay.");
        var sitemap = input.SitemapUrl is null ? null : UrlRules.Normalize(input.SitemapUrl);
        if (input.SitemapUrl is not null && (sitemap is null || sitemap.IdnHost != uri.IdnHost || input.SitemapUrl.Contains('#')))
            throw new IngestionException("A policy sitemap must be an explicitly registered HTTPS URL on the same origin.");

        new SourceDefinition(input.Key, input.Publisher, input.Publisher, uri.AbsoluteUri, input.Kind, input.Format,
            input.AllowFetch, input.PermissionNotes, input.AllowSnapshot, input.Language).Validate();

        policy.Key = input.Key;
        policy.Scope = uri.AbsoluteUri;
        policy.Prefix = input.Prefix;
        policy.Publisher = input.Publisher;
        policy.Kind = input.Kind;
        policy.Format = input.Format;
        policy.PermissionNotes = input.PermissionNotes;
        policy.AllowFetch = input.AllowFetch;
        policy.AllowAi = input.AllowAi;
        policy.AllowEvidence = input.AllowEvidence;
        policy.AllowSnapshot = input.AllowSnapshot;
        policy.ExploreLinks = input.ExploreLinks;
        policy.DelaySeconds = input.DelaySeconds;
        policy.Language = input.Language;
        policy.SitemapUrl = sitemap?.AbsoluteUri;
    }

    public static SourcePolicy? Match(IReadOnlyList<SourcePolicy> policies, string url) => policies
        .Where(policy => policy.Prefix ? url.StartsWith(policy.Scope, StringComparison.Ordinal) : url == policy.Scope)
        .OrderByDescending(policy => !policy.Prefix).ThenByDescending(policy => policy.Scope.Length)
        .ThenBy(policy => policy.AllowFetch && policy.AllowAi && policy.AllowEvidence)
        .FirstOrDefault();

    public static SourcePolicy? Permitted(IReadOnlyList<SourcePolicy> policies, DiscoveredResource resource)
    {
        if (resource.Kind == ResourceKind.Sitemap)
        {
            var sitemap = policies.Where(policy => policy.SitemapUrl == resource.Url)
                .OrderBy(policy => policy.AllowFetch).FirstOrDefault();
            // Exact registered denials also override a sitemap allowance.
            return sitemap is { AllowFetch: true } && Match(policies, resource.Url) is not { AllowFetch: false }
                ? sitemap : null;
        }
        var policy = Match(policies, resource.Url);
        return policy is { AllowFetch: true, AllowAi: true, AllowEvidence: true } ? policy : null;
    }

    public static async Task<PolicyInput[]> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var bytes = await DocumentBytes.ReadFileAsync(path, ct);
            var input = JsonSerializer.Deserialize<PolicyInput?[]>(bytes, CatalogImport.JsonOptions);
            if (input is null || input.Length > 100 || input.Any(item => item is null) ||
                input.DistinctBy(item => item?.Key).Count() != input.Length)
                throw new IngestionException("The policy file needs a JSON array of up to 100 distinct source policies.");
            return input.OfType<PolicyInput>().ToArray();
        }
        catch (JsonException error)
        {
            throw new IngestionException($"Invalid source policy JSON at {error.Path ?? "$"}.", error);
        }
    }
}
