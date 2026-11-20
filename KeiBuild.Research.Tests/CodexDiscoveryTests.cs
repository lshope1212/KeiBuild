using System.Diagnostics;
using System.Text.Json;
using KeiBuild.Ingestion;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Extraction;
using KeiBuild.Research.Fixtures;

namespace KeiBuild.Research.Tests;

public sealed class CodexDiscoveryTests
{
    internal static string Event(params (string Ref, string Url, string Title)[] rows) => JsonSerializer.Serialize(new
    {
        type = "item.completed", item = new { type = "web_search", results = rows.Select(r =>
            new { type = "text_result", ref_id = r.Ref, url = r.Url, title = r.Title, snippet = "HA4 technical catalog" }) }
    });

    internal static object Lead(string reference, string language = "en") =>
        new { reference, language, category = "oem-parts", reason = "Lists HA4 component applications." };

    internal static CodexDiscoveryProvider Mock(string events, object answer, Action<ProcessStartInfo>? inspect = null) =>
        new(new CodexCli((info, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (info.ArgumentList.First() == "login") return Task.FromResult(new CodexCli.CodexExit(0, "Logged in using ChatGPT", ""));
            inspect?.Invoke(info);
            var reply = CodexExtractionTests.Success(JsonSerializer.Serialize(answer));
            return Task.FromResult(reply with { Output = events + "\n" + reply.Output });
        }));

    [Fact]
    public async Task AcceptsOnlyObservedCanonicalUrlsAndChecksDiscoveryIsolation()
    {
        var events = Event(("turn0search0", FakeResearch.Scope + "parts#a", "HA4 parts"),
            ("turn0search1", FakeResearch.Scope + "parts#b", "Duplicate"), ("turn1view0", FakeResearch.Scope + "japanese", "HA4 純正部品"),
            ("turn0search3", "https://127.0.0.1/manual", "Private"), ("turn0search4", "file:///tmp/ha4", "Not public"));
        var provider = Mock(events, new { leads = new[] { Lead("turn0search0"), Lead("turn0search1"), Lead("turn1view0", "ja"),
            Lead("turn0search3"), Lead("turn0search4"), Lead("turn0search99") }, proposals = Array.Empty<object>() }, info =>
        {
            Assert.Contains("web_search=\"live\"", info.ArgumentList);
            Assert.Contains("permissions.extract.network.enabled=false", info.ArgumentList);
            Assert.Contains("forced_login_method=\"chatgpt\"", info.ArgumentList);
            Assert.DoesNotContain("OPENAI_API_KEY", info.Environment.Keys);
            Assert.DoesNotContain("BRAVE_SEARCH_API_KEY", info.Environment.Keys);
            foreach (var flag in new[] { "shell_tool", "unified_exec", "plugins", "apps", "browser_use", "in_app_browser", "multi_agent" })
                Assert.Equal("--disable", info.ArgumentList[info.ArgumentList.IndexOf(flag) - 1]);
        });
        var actual = await provider.DiscoverAsync(new(), ResearchBrief.Empty, default);
        Assert.Null(actual.Error);
        Assert.Collection(actual.Hits, hit => { Assert.Equal(FakeResearch.Scope + "parts", hit.Url); Assert.Equal("search-result", hit.Observation); },
            hit => { Assert.Equal("ja", hit.Language); Assert.Equal("directly-observed", hit.Observation); Assert.Equal("HA4 純正部品", hit.Title); });
        Assert.Equal(17, actual.InputTokens);
    }

    [Fact]
    public async Task RejectsFabricatedToolEventsAmbiguousReferencesAndAdditionalModelFields()
    {
        var fake = new { leads = new[] { Lead("turn0search0") }, proposals = Array.Empty<object>() };
        var noTool = await Mock("", fake).DiscoverAsync(new(), ResearchBrief.Empty, default);
        Assert.Equal(403, noTool.Error?.HttpStatus);
        var ambiguous = Event(("turn0search0", FakeResearch.Scope + "parts", "a"), ("turn0search0", FakeResearch.Scope + "manual", "b"));
        Assert.NotNull((await Mock(ambiguous, fake).DiscoverAsync(new(), ResearchBrief.Empty, default)).Error);
        var valid = Event(("turn0search0", FakeResearch.Scope + "manual", "HA4"));
        var forged = await Mock(valid, new { leads = new[] { Lead("turn0search42") }, proposals = Array.Empty<object>() })
            .DiscoverAsync(new(), ResearchBrief.Empty, default);
        Assert.Empty(forged.Hits);
        Assert.True(forged.Error?.IsTransient);
        Assert.Equal(4, forged.OutputTokens);
        var schemaViolation = new { leads = new[] { new { reference = "turn0search0", language = "en", category = "identity",
            reason = "x", url = "https://invented.example/" } }, proposals = Array.Empty<object>() };
        await Assert.ThrowsAsync<IngestionException>(() => Mock(valid, schemaViolation).SearchAsync(new(), default));
    }

    [Fact]
    public void ResolvesNativePackageBehindWindowsNpmShim()
    {
        var dir = Directory.CreateTempSubdirectory("keibuild-codex-shim-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "codex.cmd"), "");
            var bin = Path.Combine(dir.FullName, "node_modules", "@openai", "codex", "node_modules", "@openai",
                "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin");
            Directory.CreateDirectory(bin);
            var exe = Path.Combine(bin, "codex.exe");
            File.WriteAllText(exe, "");
            Assert.Equal(exe, CodexCli.ResolveWindowsNative(dir.FullName, System.Runtime.InteropServices.Architecture.X64));
            Assert.Null(CodexCli.ResolveWindowsNative(dir.FullName, System.Runtime.InteropServices.Architecture.Arm64));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ProvidersAreIndependentAndBraveIsExplicit()
    {
        Assert.Equal("codex", CommandLine.Parse(["run", "sample"]).SearchProvider);
        var command = CommandLine.Parse(["run", "sample", "--search-provider", "brave", "--provider", "codex"]);
        Assert.Equal("brave", command.SearchProvider);
        Assert.Equal("codex", command.Provider);
        Assert.Throws<IngestionException>(() => CommandLine.Parse(["run", "sample", "--search-provider", "unknown"]));
    }
}

