using System.Runtime.CompilerServices;
using System.Text;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Checkpoint demonstration (not just assertions): runs XmlContentExtractor
/// against the real sample files in Maia.Tests/Samples and dumps the ACTUAL
/// extracted values to a results file for human review, mirroring the two
/// operator use cases:
///   • use case 2 (content predicate): /file/status/code + /file/header/invoiceId
///   • use case 1 (filename signals failure): identifier pulled from inside
/// It also asserts the key values so a regression breaks the build.
/// </summary>
public class XmlContentExtractorSamplesTests
{
    private static readonly XmlContentExtractor Extractor = new(NullLogger<XmlContentExtractor>.Instance);

    private static string SamplesDir([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "Samples"));

    [Fact]
    public async Task DumpRealFileExtractions()
    {
        var dir = SamplesDir();
        var sb = new StringBuilder();
        sb.AppendLine("file                       | locator                       | extracted value");
        sb.AppendLine("---------------------------+-------------------------------+----------------");

        async Task Row(string file, string locator)
        {
            var path = Path.Combine(dir, file);
            string? value;
            try   { value = await Extractor.ExtractAsync(path, locator); }
            catch (Exception ex) { value = $"<throws {ex.GetType().Name}>"; }
            sb.AppendLine($"{file,-26} | {locator,-29} | {value ?? "<null>"}");
        }

        // Use case 2 — invoice with error status (predicate target + identifier)
        await Row("invoice-error.xml", "/file/status/code");
        await Row("invoice-error.xml", "/file/header/invoiceId");
        // Use case 2 — healthy invoice (predicate would NOT fire on OK)
        await Row("invoice-ok.xml",    "/file/status/code");
        await Row("invoice-ok.xml",    "/file/header/invoiceId");
        // Use case 1 — *WARNING*.xml filename is the signal; identifier from inside
        await Row("WARNING_20260606.xml", "/order/@id");
        await Row("WARNING_20260606.xml", "/order/header/orderRef");
        // Edge — malformed file yields null (skipped, no failure)
        await Row("malformed.xml",     "/file/status/code");
        // Edge — a locator that matches nothing
        await Row("invoice-error.xml", "/file/header/nope");

        var outPath = Path.Combine(Path.GetTempPath(), "maia-xml-samples-output.txt");
        await File.WriteAllTextAsync(outPath, sb.ToString());

        // Assertions so this doubles as a regression guard.
        Assert.Equal("ERROR",        await Extractor.ExtractAsync(Path.Combine(dir, "invoice-error.xml"), "/file/status/code"));
        Assert.Equal("INV-2026-001", await Extractor.ExtractAsync(Path.Combine(dir, "invoice-error.xml"), "/file/header/invoiceId"));
        Assert.Equal("OK",           await Extractor.ExtractAsync(Path.Combine(dir, "invoice-ok.xml"),    "/file/status/code"));
        Assert.Equal("ORD-88134",    await Extractor.ExtractAsync(Path.Combine(dir, "WARNING_20260606.xml"), "/order/@id"));
        Assert.Equal("PO-77421",     await Extractor.ExtractAsync(Path.Combine(dir, "WARNING_20260606.xml"), "/order/header/orderRef"));
        Assert.Null(await Extractor.ExtractAsync(Path.Combine(dir, "malformed.xml"), "/file/status/code"));
        Assert.Null(await Extractor.ExtractAsync(Path.Combine(dir, "invoice-error.xml"), "/file/header/nope"));
    }
}
