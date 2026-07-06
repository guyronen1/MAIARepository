using Maia.Core.Interfaces;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Pins XmlContentExtractor's v1 behaviour: XPath value extraction (element,
/// attribute, text, CDATA, functions), the null-on-miss / null-on-malformed
/// contract, whitespace trimming, and the hard 5MB cap (throws, not null).
/// </summary>
public class XmlContentExtractorTests
{
    private static readonly XmlContentExtractor Extractor = new(NullLogger<XmlContentExtractor>.Instance);

    private static async Task<string?> Extract(string xml, string locator)
    {
        var path = Path.Combine(Path.GetTempPath(), $"maia-xml-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(path, xml);
        try { return await Extractor.ExtractAsync(path, locator); }
        finally { File.Delete(path); }
    }

    // ── Happy paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractsElementValue()
    {
        var xml = "<file><status><code>ERROR</code></status></file>";
        Assert.Equal("ERROR", await Extract(xml, "/file/status/code"));
    }

    [Fact]
    public async Task ExtractsNestedIdentifier()
    {
        var xml = "<file><header><invoiceId>INV-2026-001</invoiceId></header></file>";
        Assert.Equal("INV-2026-001", await Extract(xml, "/file/header/invoiceId"));
    }

    [Fact]
    public async Task ExtractsAttributeValue()
    {
        var xml = "<order id=\"ORD-42\"><line>x</line></order>";
        Assert.Equal("ORD-42", await Extract(xml, "/order/@id"));
    }

    [Fact]
    public async Task ExtractsCdataValue()
    {
        var xml = "<file><note><![CDATA[ batch failed ]]></note></file>";
        Assert.Equal("batch failed", await Extract(xml, "/file/note"));  // trimmed
    }

    [Fact]
    public async Task ExtractsViaXPathFunction()
    {
        var xml = "<file><header><invoiceId>INV-9</invoiceId></header></file>";
        Assert.Equal("INV-9", await Extract(xml, "string(/file/header/invoiceId)"));
    }

    [Fact]
    public async Task TakesFirstWhenLocatorMatchesMany()
    {
        var xml = "<root><item>first</item><item>second</item></root>";
        Assert.Equal("first", await Extract(xml, "/root/item"));
    }

    [Fact]
    public async Task TrimsSurroundingWhitespace()
    {
        var xml = "<file><code>   PADDED   </code></file>";
        Assert.Equal("PADDED", await Extract(xml, "/file/code"));
    }

    // ── Namespace-blind matching (v1: namespaces stripped before XPath) ─────────

    [Fact]
    public async Task DefaultNamespace_PlainXPathMatches()
    {
        // Operator writes /file/status/code; the XML has a default namespace.
        var xml = "<file xmlns=\"urn:acme:invoices\"><status><code>ERROR</code></status></file>";
        Assert.Equal("ERROR", await Extract(xml, "/file/status/code"));
    }

    [Fact]
    public async Task PrefixedNamespace_PlainXPathMatches()
    {
        // XML uses a prefix (<ns:file>); operator still writes plain names.
        var xml = "<ns:file xmlns:ns=\"urn:acme:invoices\"><ns:header><ns:invoiceId>INV-7</ns:invoiceId></ns:header></ns:file>";
        Assert.Equal("INV-7", await Extract(xml, "/file/header/invoiceId"));
    }

    [Fact]
    public async Task AttributeOnNamespacedXml_PlainXPathMatches()
    {
        var xml = "<order xmlns=\"urn:acme:orders\" id=\"ORD-99\"><line>x</line></order>";
        Assert.Equal("ORD-99", await Extract(xml, "/order/@id"));
    }

    [Fact]
    public async Task MixedNamespacedAndPlain_MatchUniformly()
    {
        // Root + part of the tree namespaced, a child not — all addressable plainly.
        var xml = "<file xmlns=\"urn:acme:invoices\"><header xmlns=\"\"><invoiceId>INV-8</invoiceId></header><status><code>OK</code></status></file>";
        Assert.Equal("INV-8", await Extract(xml, "/file/header/invoiceId"));
        Assert.Equal("OK",    await Extract(xml, "/file/status/code"));
    }

    // ── Misses return null (not an exception) ───────────────────────────────────

    [Fact]
    public async Task MissingNode_ReturnsNull()
    {
        var xml = "<file><status><code>OK</code></status></file>";
        Assert.Null(await Extract(xml, "/file/header/invoiceId"));
    }

    [Fact]
    public async Task EmptyElement_ReturnsNull()
    {
        var xml = "<file><code></code></file>";
        Assert.Null(await Extract(xml, "/file/code"));   // empty value collapses to null
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankLocator_ReturnsNull(string locator)
    {
        var xml = "<file><code>X</code></file>";
        Assert.Null(await Extract(xml, locator));
    }

    [Fact]
    public async Task InvalidXPath_ReturnsNull()
    {
        var xml = "<file><code>X</code></file>";
        Assert.Null(await Extract(xml, "/file/[[[bad"));
    }

    [Fact]
    public async Task MalformedXml_ReturnsNull()
    {
        var xml = "<file><code>X</code>";   // unclosed root
        Assert.Null(await Extract(xml, "/file/code"));
    }

    [Fact]
    public async Task FileNotFound_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"maia-missing-{Guid.NewGuid():N}.xml");
        Assert.Null(await Extractor.ExtractAsync(missing, "/file/code"));
    }

    // ── Hard size cap throws (distinct from a miss) ─────────────────────────────

    [Fact]
    public async Task OversizeFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maia-big-{Guid.NewGuid():N}.xml");
        await File.WriteAllBytesAsync(path, new byte[XmlContentExtractor.MaxFileSizeBytes + 1]);
        try
        {
            var ex = await Assert.ThrowsAsync<FileContentTooLargeException>(
                () => Extractor.ExtractAsync(path, "/file/code"));
            Assert.Equal(XmlContentExtractor.MaxFileSizeBytes, ex.CapBytes);
            Assert.True(ex.SizeBytes > ex.CapBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task JustUnderCap_DoesNotThrow()
    {
        // A valid small doc is obviously under the cap — proves the cap path
        // isn't tripping on normal files.
        var xml = "<file><code>FINE</code></file>";
        Assert.Equal("FINE", await Extract(xml, "/file/code"));
    }

    // ── Locator syntax validation (save-time check) ─────────────────────────────

    [Theory]
    [InlineData("/file/status/code")]
    [InlineData("//KOD-SHGIHA-BERAMAT-RESHUMA")]
    [InlineData("/order/@id")]
    [InlineData("string(/file/header/invoiceId)")]
    [InlineData("")]            // empty = "no locator" = valid
    [InlineData("   ")]
    public void ValidateLocator_AcceptsValidXPath(string locator)
        => Assert.Null(Extractor.ValidateLocator(locator));

    [Theory]
    [InlineData("\\\\KOD-SHGIHA-BERAMAT-RESHUMA")]   // the real `\\` vs `//` typo
    [InlineData("\\MISPAR-MISLAKA")]
    [InlineData("/file/[[[bad")]
    [InlineData("///")]
    public void ValidateLocator_RejectsMalformedXPath(string locator)
        => Assert.NotNull(Extractor.ValidateLocator(locator));
}
