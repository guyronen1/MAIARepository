using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Infrastructure.Fix;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Covers CopyFileExecutor's payload parsing, atomic copy semantics, and
/// error cases. Mocks IPlaceholderResolver so tests don't depend on a DB —
/// the resolver behaviour itself is exercised in PlaceholderResolverTests.
/// </summary>
public class CopyFileExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IPlaceholderResolver> _resolver = new();

    public CopyFileExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "maia-copyfile-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* swallow */ }
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_HappyPath_CopiesAndReturnsTrue()
    {
        var src  = WriteFile("src.txt", "hello");
        var dest = Path.Combine(_tempDir, "out", "dest.txt");
        SetupResolver(src, dest);

        var ok = await CreateSut().ExecuteAsync($"{src}|{dest}", MakeRec());

        Assert.True(ok.Success);
        Assert.True(File.Exists(dest));
        Assert.Equal("hello", await File.ReadAllTextAsync(dest));
        // Atomic-copy intermediate file must be cleaned up.
        Assert.False(File.Exists(dest + ".tmp"));
    }

    [Fact]
    public async Task Execute_SourceMissing_ReturnsFalse()
    {
        var src  = Path.Combine(_tempDir, "does-not-exist.txt");
        var dest = Path.Combine(_tempDir, "dest.txt");
        SetupResolver(src, dest);

        var ok = await CreateSut().ExecuteAsync($"{src}|{dest}", MakeRec());

        Assert.False(ok.Success);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Execute_DestExists_OverwritesByDefault()
    {
        var src  = WriteFile("src.txt", "new content");
        var dest = WriteFile("dest.txt", "old content");
        SetupResolver(src, dest);

        var ok = await CreateSut().ExecuteAsync($"{src}|{dest}", MakeRec());

        Assert.True(ok.Success);
        Assert.Equal("new content", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task Execute_DestDirectoryMissing_AutoCreated()
    {
        var src  = WriteFile("src.txt", "x");
        var dest = Path.Combine(_tempDir, "nested", "deep", "dest.txt");
        SetupResolver(src, dest);

        var ok = await CreateSut().ExecuteAsync($"{src}|{dest}", MakeRec());

        Assert.True(ok.Success);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public async Task Execute_PayloadMalformed_NoPipe_ReturnsFalse()
    {
        var ok = await CreateSut().ExecuteAsync("no-pipe-anywhere", MakeRec());
        Assert.False(ok.Success);
    }

    [Fact]
    public async Task Execute_PayloadMalformed_EmptyDest_ReturnsFalse()
    {
        var ok = await CreateSut().ExecuteAsync("source|", MakeRec());
        Assert.False(ok.Success);
    }

    [Fact]
    public async Task Execute_PayloadEmpty_ReturnsFalse()
    {
        var ok = await CreateSut().ExecuteAsync("", MakeRec());
        Assert.False(ok.Success);
    }

    [Fact]
    public async Task Execute_PreCancelledToken_ReturnsFalseQuicklyAndCleansUpTmp()
    {
        // Pre-cancelled token must propagate through CopyToAsync(stream, ct)
        // and abandon the copy. Critically: the .tmp file must be cleaned
        // up so we don't litter the destination directory.
        var src  = WriteFile("src.txt", new string('x', 100_000)); // big enough to need multiple chunks
        var dest = Path.Combine(_tempDir, "dest.txt");
        SetupResolver(src, dest);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = await CreateSut().ExecuteAsync($"{src}|{dest}", MakeRec(), cts.Token);
        sw.Stop();

        Assert.False(ok.Success);
        Assert.False(File.Exists(dest), "destination must not exist on cancel");
        Assert.False(File.Exists(dest + ".tmp"), "tmp must be cleaned up on cancel");
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"cancel should be fast, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Execute_PlaceholderUnresolved_ReturnsFalse()
    {
        // Resolver throws on the SOURCE call (strict mode). Executor must
        // catch, log, and return false — not propagate the exception.
        _resolver.Setup(r => r.ResolveOrThrowAsync(
                It.IsAny<string>(),
                It.IsAny<AiRecommendation>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PlaceholderUnresolvedException("test error")
                { PlaceholderName = "sourceFilePath" });

        var ok = await CreateSut().ExecuteAsync("{sourceFilePath}|dest", MakeRec());

        Assert.False(ok.Success);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private void SetupResolver(string sourcePath, string destPath)
    {
        // SOURCE goes through ResolveOrThrowAsync (strict on {sourceFilePath}).
        // DEST goes through ResolveAsync (non-strict).
        _resolver.Setup(r => r.ResolveOrThrowAsync(
                It.IsAny<string>(),
                It.IsAny<AiRecommendation>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourcePath);
        _resolver.Setup(r => r.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<AiRecommendation>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(destPath);
    }

    private CopyFileExecutor CreateSut() =>
        new(_resolver.Object, NullLogger<CopyFileExecutor>.Instance);

    private static AiRecommendation MakeRec() => new()
    {
        RecommendationId = 1,
        FailureId        = 1,
        ErrorTypeId      = 1,
        SuggestedAction  = "test",
        FixCategory      = FixCategory.FileRepair,
        ConfidenceScore  = 0.9m,
    };
}
