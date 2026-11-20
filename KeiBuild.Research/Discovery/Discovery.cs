using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Research.Data;

namespace KeiBuild.Research.Discovery;

public sealed record TopicPlan(string Key, string Title, string[] English, string[] Japanese);
public sealed record PlannedQuery(string Topic, string Language, string Text);

public sealed record ResearchTarget(string Make, string Model, string Chassis, string Engine, string Era,
    string EnglishName, string JapaneseName)
{
    public static ResearchTarget Default { get; } = new("Honda", "Acty Truck", "HA4", "E07A",
        "1990s reference period", "Honda Acty", "ホンダ アクティ");

    public static async Task<ResearchTarget> ReadAsync(string? path, CancellationToken ct)
    {
        if (path is null) return Default;
        using var file = File.OpenRead(path);
        ResearchTarget? target;
        try
        {
            target = await JsonSerializer.DeserializeAsync<ResearchTarget>(file, JsonSerializerOptions.Web, ct);
        }
        catch (JsonException ex) { throw new IngestionException("Invalid research target JSON.", ex); }
        if (target is null || !Regex.IsMatch(target.Chassis ?? "", @"\A[A-Z0-9-]{2,16}\z") ||
            new[] { target.Make, target.Model, target.Engine, target.Era, target.EnglishName, target.JapaneseName }
                .Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 100 || s.Any(char.IsControl)))
            throw new IngestionException("Target requires make, model, chassis, engine, era, EnglishName and JapaneseName.");
        return target;
    }

    public static IReadOnlyList<TopicPlan> Topics { get; } =
    [
        new("identity", "Vehicle identity", ["specifications dimensions drivetrain", "production chassis serial trims transmission"], ["主要諸元 寸法 駆動方式", "車台番号 型式 グレード"]),
        new("oem-parts", "OEM parts", ["OEM parts catalog", "parts diagram superseded part number"], ["純正部品 品番 部品図", "パーツカタログ 互換 代替"]),
        new("maintenance", "Maintenance", ["{engine} timing belt water pump part number", "service intervals ignition cooling filters fluids"], ["{engine} タイミングベルト ウォーターポンプ", "整備 点検 交換時期 プラグ フィルター"]),
        new("mechanical", "Mechanical systems", ["suspension specifications brake replacement", "steering electrical transmission component"], ["足回り ブレーキ 諸元 部品", "ステアリング 電装 トランスミッション"]),
        new("modifications", "Modifications", ["lift kit wheel tire fitment exclusions", "springs shocks exhaust lighting compatibility"], ["リフトアップ ホイール タイヤ 適合", "サスペンション マフラー 取付 条件"]),
        new("troubleshooting", "Troubleshooting", ["common problems diagnostic symptoms", "cooling ignition drivetrain troubleshooting"], ["故障 診断 症状 修理", "冷却 点火 駆動 不具合"]),
        new("japanese", "Japanese references", ["Japanese service manual technical bulletin", "Japan manufacturer technical data parts application"], ["整備 マニュアル サービス資料", "技術情報 整備資料 純正部品"])
    ];

    public IEnumerable<PlannedQuery> Queries()
    {
        // Put both languages for each topic in the first sweep before generating narrower variants.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var topic in Topics)
            {
                yield return new(topic.Key, "en", $"{EnglishName} {Chassis} {topic.English[pass].Replace("{engine}", Engine)}");
                yield return new(topic.Key, "ja", $"{JapaneseName} {Chassis} {topic.Japanese[pass].Replace("{engine}", Engine)}");
            }
        }
    }
}

public sealed record SearchHit(string Url, string Title, string Snippet, string? Publisher = null,
    string? Language = null, string? Category = null, string? Reason = null, string? Reference = null, string? Observation = null);

public sealed record ProposedSearch(string Text, string Language, string Category, string Reason);
public sealed record SearchOutcome(IReadOnlyList<SearchHit> Hits, IReadOnlyList<ProposedSearch> Proposals,
    int InputTokens = 0, int OutputTokens = 0, IngestionException? Error = null);

public interface ISearchProvider
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct);

    async Task<SearchOutcome> DiscoverAsync(SearchQuery query, ResearchBrief brief, CancellationToken ct) =>
        new(await SearchAsync(query, ct), []);
}

public sealed class BraveSearchProvider(HttpClient client, string key) : ISearchProvider
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        var country = query.Language == "ja" ? "JP" : "US";
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query.Text)}&count=10&country={country}&search_lang={query.Language}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Subscription-Token", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var retry = response.Headers.RetryAfter;
                throw new IngestionException($"Brave Search returned HTTP {status}.")
                {
                    HttpStatus = status,
                    IsTransient = status is 408 or 429 or >= 500,
                    RetryAt = retry?.Date ?? (retry?.Delta is { } delay ? DateTimeOffset.UtcNow + delay : null)
                };
            }
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await DocumentBytes.ReadAsync(stream, deadline.Token);
            return ParseResults(bytes);
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new IngestionException("Brave Search timed out.", error) { IsTransient = true };
        }
        catch (HttpRequestException error)
        {
            throw new IngestionException("Brave Search could not be reached.", error) { IsTransient = true };
        }
    }

    public static IReadOnlyList<SearchHit> ParseResults(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var data = JsonDocument.Parse(json);
            var root = data.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            if (!root.TryGetProperty("web", out var web))
                return [];
            if (web.ValueKind != JsonValueKind.Object || !web.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                throw new JsonException();

            return results.EnumerateArray().Take(20).Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new SearchHit(Get(item, "url"), Clean(Get(item, "title"), 200), Clean(Get(item, "description"), 500)))
                .Where(item => UrlRules.Normalize(item.Url) is not null)
                .DistinctBy(item => UrlRules.Normalize(item.Url)!.AbsoluteUri).ToArray();
        }
        catch (JsonException error)
        {
            throw new IngestionException("Brave Search returned an unexpected results document.", error) { IsTransient = true };
        }
    }

    private static string Get(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    internal static string Clean(string text, int maximum)
    {
        text = WebUtility.HtmlDecode(text.Replace("<strong>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("</strong>", "", StringComparison.OrdinalIgnoreCase)).Replace("\0", "", StringComparison.Ordinal);
        return text.Length <= maximum ? text : text[..maximum];
    }
}

public static partial class Relevance
{
    public static (ResourceKind Kind, int Score) Classify(Uri url, string title, string snippet, string chassis, string engine)
    {
        var text = $"{title} {snippet} {url.AbsolutePath}";
        if (!Mentions(text, chassis))
            return (ResourceKind.Irrelevant, 0);
        var score = 50;
        if (TechnicalTerms().IsMatch(text))
            score += 20;
        if (text.Contains(engine, StringComparison.OrdinalIgnoreCase))
            score += 8;
        var host = url.IdnHost;
        if (host is "global.honda" or "honda.co.jp" || host.EndsWith(".honda.co.jp", StringComparison.Ordinal))
            return (ResourceKind.Manufacturer, score + 15);
        if (CommunityTerms().IsMatch(host + " " + text))
            return (ResourceKind.Community, score);
        if (VendorTerms().IsMatch(host + " " + text))
            return (ResourceKind.Vendor, score + 5);
        return (ResourceKind.Technical, score);
    }

    public static bool Mentions(string text, string chassis) =>
        Regex.IsMatch(text, $"(?<![a-z0-9]){Regex.Escape(chassis)}(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex("part|specification|engine|drivetrain|brake|suspension|manual|maintenance|fitment|repair|品番|部品|整備|故障|適合|諸元", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalTerms();

    [GeneratedRegex("forum|reddit|discussion|掲示板|みんカラ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommunityTerms();

    [GeneratedRegex("shop|add to cart|store|catalog|通販|販売", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorTerms();
}
