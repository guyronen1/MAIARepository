using System.Text.Json;
using Maia.API.Controllers;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Pins the scan-source pickers: connection-names leaks keys only (never the
/// connection-string values), and browse-folders lists directories only, honours
/// FolderBrowser:Roots as a hard boundary (incl. the "C:\data" vs "C:\database"
/// prefix trap), and maps folder errors to 400/404 rather than 403.
/// </summary>
public sealed class ScanSourceLookupsControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maia-browse-" + Guid.NewGuid().ToString("N"));

    public ScanSourceLookupsControllerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "allowed", "logs"));
        Directory.CreateDirectory(Path.Combine(_root, "allowed", "Archive"));
        Directory.CreateDirectory(Path.Combine(_root, "allowedTwin"));
        File.WriteAllText(Path.Combine(_root, "allowed", "app.log"), "x");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private static ScanSourceLookupsController Ctrl(Dictionary<string, string?> cfg, ISourceSchemaReader? reader = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(),
            reader ?? Mock.Of<ISourceSchemaReader>(),
            NullLogger<ScanSourceLookupsController>.Instance);

    private ScanSourceLookupsController RootedCtrl() =>
        Ctrl(new() { ["FolderBrowser:Roots:0"] = Path.Combine(_root, "allowed") });

    private static JsonElement Json(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement;

    [Fact]
    public void ConnectionNames_returns_sorted_keys_only_and_skips_blank_values()
    {
        var ok = Assert.IsType<OkObjectResult>(Ctrl(new()
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=secret;Password=p@ss;",
            ["ConnectionStrings:b2bTest"]           = "Server=other;",
            ["ConnectionStrings:Empty"]             = "",
        }).GetConnectionNames());

        var entries = Assert.IsType<ScanSourceLookupsController.ConnectionEntry[]>(ok.Value);
        Assert.Equal(new[] { "b2bTest", "DefaultConnection" }, entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.Equal("SqlServer", e.Provider));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(ok.Value));
    }

    [Fact]
    public void ConnectionNames_reports_each_provider_and_hides_the_companion_keys()
    {
        var ok = Assert.IsType<OkObjectResult>(Ctrl(new()
        {
            ["ConnectionStrings:Billing"]              = "Data Source=BILLPRD;User Id=u;Password=secret;",
            ["ConnectionStrings:Billing_ProviderName"] = "Oracle",
            ["ConnectionStrings:Main"]                 = "Server=x;",
            ["ConnectionStrings:Main_ProviderName"]    = "System.Data.SqlClient",
            ["ConnectionStrings:Odd"]                  = "Server=x;",
            ["ConnectionStrings:Odd_ProviderName"]     = "MySql",
        }).GetConnectionNames());

        var entries = Assert.IsType<ScanSourceLookupsController.ConnectionEntry[]>(ok.Value);
        Assert.Equal(new[] { ("Billing", "Oracle"), ("Main", "SqlServer"), ("Odd", "Unknown") },
            entries.Select(e => (e.Name, e.Provider)));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(ok.Value));
    }

    [Fact]
    public void Browse_lists_subfolders_only_sorted_with_parent()
    {
        var allowed = Path.Combine(_root, "allowed");
        var j = Json(Assert.IsType<OkObjectResult>(Ctrl(new()).BrowseFolders(allowed)).Value);

        Assert.Equal(allowed, j.GetProperty("path").GetString());
        Assert.Equal(Path.GetFullPath(_root), j.GetProperty("parent").GetString());
        var names = j.GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "Archive", "logs" }, names);   // app.log (a file) is not listed
        Assert.False(j.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Browse_without_path_returns_configured_roots_only()
    {
        var j = Json(Assert.IsType<OkObjectResult>(RootedCtrl().BrowseFolders(null)).Value);

        Assert.Equal(JsonValueKind.Null, j.GetProperty("path").ValueKind);
        Assert.Equal(Path.Combine(_root, "allowed"),
            Assert.Single(j.GetProperty("roots").EnumerateArray()).GetProperty("fullPath").GetString());
    }

    [Fact]
    public void Browse_at_a_configured_root_has_no_parent()
    {
        var j = Json(Assert.IsType<OkObjectResult>(RootedCtrl().BrowseFolders(Path.Combine(_root, "allowed"))).Value);
        Assert.Equal(JsonValueKind.Null, j.GetProperty("parent").ValueKind);
    }

    [Theory]
    [InlineData("")]               // the parent of the root
    [InlineData("allowedTwin")]    // shares the root's prefix — must not be admitted
    [InlineData(@"allowed\..\allowedTwin")]
    public void Browse_outside_configured_roots_is_rejected(string rel)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(RootedCtrl().BrowseFolders(Path.Combine(_root, rel)));
        Assert.Equal("PathOutsideRoots", Json(bad.Value).GetProperty("error").GetString());
    }

    [Fact]
    public void Browse_missing_folder_is_404()
    {
        var nf = Assert.IsType<NotFoundObjectResult>(Ctrl(new()).BrowseFolders(Path.Combine(_root, "nope")));
        Assert.Equal("FolderNotFound", Json(nf.Value).GetProperty("error").GetString());
    }

    // ── Source columns (reader mocked: the catalog SQL can't run on InMemory) ──

    private static ScanSourceLookupsController ColumnsCtrl(TableColumnsResult result, out Mock<ISourceSchemaReader> reader)
    {
        reader = new Mock<ISourceSchemaReader>();
        reader.Setup(r => r.GetColumnsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(result);
        return Ctrl(new(), reader.Object);
    }

    [Fact]
    public async Task Columns_ok_returns_the_column_list_and_passes_source_and_table_through()
    {
        var cols = new[]
        {
            new TableColumnInfo("FileId", "int", false, ColumnTypeCategory.Number),
            new TableColumnInfo("Status", "nvarchar(50)", true, ColumnTypeCategory.String),
        };
        var ctrl = ColumnsCtrl(TableColumnsResult.Found(cols), out var reader);

        var ok = Assert.IsType<OkObjectResult>(await ctrl.GetSourceColumns(7, "dbo.Files", default));

        var arr = Json(ok.Value).EnumerateArray().ToArray();
        Assert.Equal(new[] { "FileId", "Status" }, arr.Select(a => a.GetProperty("name").GetString()));
        Assert.Equal("number", arr[0].GetProperty("typeCategory").GetString());
        Assert.Equal("nvarchar(50)", arr[1].GetProperty("dataType").GetString());
        Assert.True(arr[1].GetProperty("isNullable").GetBoolean());
        reader.Verify(r => r.GetColumnsAsync(7, "dbo.Files", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(TableColumnsStatus.SourceNotFound)]
    [InlineData(TableColumnsStatus.TableNotFound)]
    public async Task Columns_missing_source_or_table_is_404_with_error_code(TableColumnsStatus status)
    {
        var nf = Assert.IsType<NotFoundObjectResult>(
            await ColumnsCtrl(TableColumnsResult.Fail(status, "gone"), out _).GetSourceColumns(1, "dbo.Files", default));
        var j = Json(nf.Value);
        Assert.Equal(status.ToString(), j.GetProperty("error").GetString());
        Assert.Equal("gone", j.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(TableColumnsStatus.TableNotResolvable)]
    [InlineData(TableColumnsStatus.TableRequired)]
    [InlineData(TableColumnsStatus.NotDatabaseSource)]
    [InlineData(TableColumnsStatus.ConnectionUnresolved)]
    [InlineData(TableColumnsStatus.ConnectionFailed)]
    [InlineData(TableColumnsStatus.SchemaReadFailed)]
    public async Task Columns_dead_ends_are_400_never_403(TableColumnsStatus status)
    {
        var result = await ColumnsCtrl(TableColumnsResult.Fail(status, "why"), out _).GetSourceColumns(1, "x", default);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(status.ToString(), Json(bad.Value).GetProperty("error").GetString());
    }

    [Fact]
    public void Browse_relative_path_is_400()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(Ctrl(new()).BrowseFolders(@"relative\path"));
        Assert.Equal("InvalidPath", Json(bad.Value).GetProperty("error").GetString());
    }
}
