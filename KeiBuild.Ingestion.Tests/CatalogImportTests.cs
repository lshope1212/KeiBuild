using KeiBuild.Catalog;

namespace KeiBuild.Ingestion.Tests;

public sealed class CatalogImportTests
{
    private readonly CatalogImport import = new(Fixture.ChassisCodes);

    [Fact]
    public async Task ExistingExampleRoundTripsThroughSharedContract()
    {
        var json = await File.ReadAllTextAsync(Fixture.PathFor("example-import.json"), Fixture.Ct);
        var entries = import.Parse(json);
        Assert.Equal("P561A", Assert.Single(entries).PartNumber);
        Assert.Equal(entries, import.Parse(import.Serialize(entries)));
    }

    [Theory]
    [InlineData("ftp://example.com/data")]
    [InlineData("https://user:secret@example.com/data")]
    [InlineData("/relative/source")]
    public void InvalidSourceUrlsAreRejected(string url)
    {
        var entry = Part() with { Source = Part().Source with { Url = url } };
        Assert.Throws<CatalogImportException>(() => import.Validate(entry, 1));
    }

    [Fact]
    public void InvalidEnumsAndOverlongFieldsAreRejected()
    {
        var entry = Part();
        Assert.Throws<CatalogImportException>(() => import.Validate(entry with { Kind = (EntryKind)42 }, 1));
        Assert.Throws<CatalogImportException>(() => import.Validate(entry with { Source = entry.Source with { Kind = (SourceKind)42 } }, 1));
        Assert.Throws<CatalogImportException>(() => import.Validate(entry with { Title = new string('a', 101) }, 1));
        Assert.Throws<CatalogImportException>(() => import.Validate(entry with { PartNumber = "   " }, 1));
        Assert.Throws<CatalogImportException>(() => import.Parse("[]"));
    }

    [Fact]
    public void ClaimKeyPreservesPartNumberPunctuationSourceAndConditions()
    {
        var key = CatalogImport.ClaimKey(Part());
        Assert.Equal(key, CatalogImport.ClaimKey(Part() with { Manufacturer = "  fixture  ", Conditions = " HA4   1998 " }));
        Assert.NotEqual(key, CatalogImport.ClaimKey(Part() with { PartNumber = "00TEST" }));
        Assert.NotEqual(key, CatalogImport.ClaimKey(Part() with { Conditions = "HA4 1997" }));
        Assert.NotEqual(key, CatalogImport.ClaimKey(Part() with { Source = Part().Source with { Url = "https://fixture.example/other" } }));
    }

    private static FitmentEntry Part() => new(
        "HA4", EntryKind.Part, "Synthetic filter", "Fixture", "Cabin", "HA4 1998",
        new SourceReference("Fixture source", "https://fixture.example/test", SourceKind.Vendor, "Table", "Evidence"),
        "00-TEST");
}
