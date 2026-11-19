using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Ingestion.Tests;

internal static class Fixture
{
    internal static readonly string[] ChassisCodes = ["HA3", "HA4", "DD51T", "DA16T"];
    internal static readonly CancellationToken Ct = CancellationToken.None;

    internal static string PathFor(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    internal static Task<SourceDefinition> SourceAsync() =>
        SourceDefinition.LoadAsync(PathFor("sources.example.json"), "synthetic-ha4", Ct);
}

internal sealed class TestDirectory : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("KeiBuild-test-").FullName;
    internal string PathFor(string name) => Path.Combine(Root, name);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
