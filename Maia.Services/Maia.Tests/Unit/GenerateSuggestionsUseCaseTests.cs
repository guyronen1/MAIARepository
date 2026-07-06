using Maia.Application.Remediation;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

public class GenerateSuggestionsUseCaseTests
{
    private readonly Mock<IRecommendationRepository> _repoMock      = new();
    private readonly Mock<IFixCatalogue>             _catalogueMock = new();

    private GenerateSuggestionsUseCase CreateSut() =>
        new(_repoMock.Object, _catalogueMock.Object,
            NullLogger<GenerateSuggestionsUseCase>.Instance);

    private static ClassificationResult MakeResult(
        string errorCode, int errorTypeId = 1, double confidence = 0.9) =>
        new()
        {
            FailureId     = 1,
            JobId         = 1,
            JobTypeId     = 1,
            ErrorTypeId   = errorTypeId,
            ErrorTypeCode = errorCode,
            RawError      = "some error",
            Confidence    = confidence,
        };

    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FileNotFound", FixCategory.FileRepair, false)]
    [InlineData("DbConnection", FixCategory.Retry,      true)]
    [InlineData("Timeout",      FixCategory.Retry,      true)]
    [InlineData("Transform",    FixCategory.Manual,     false)]
    [InlineData("Unknown",      FixCategory.Manual,     false)]
    public async Task GenerateSuggestions_MapsErrorTypeToCorrectCategory(
        string errorCode, FixCategory expectedCategory, bool expectedAutoHeal)
    {
        _catalogueMock
            .Setup(c => c.GetEntryAsync(errorCode, It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FixCatalogueEntry("Fix it.", expectedCategory, 0.0, expectedAutoHeal));

        AiRecommendation? saved = null;
        _repoMock.Setup(r => r.SaveAsync(It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()))
            .Callback<AiRecommendation, CancellationToken>((rec, _) => saved = rec)
            .Returns(Task.CompletedTask);

        await CreateSut().ExecuteAsync([MakeResult(errorCode)]);

        Assert.NotNull(saved);
        Assert.Equal(expectedCategory, saved!.FixCategory);
        Assert.Equal(expectedAutoHeal, saved.AutoFixAvailable);
        Assert.Equal(1, saved.FailureId);
    }

    [Fact]
    public async Task GenerateSuggestions_SavesOneRecommendationPerResult()
    {
        _catalogueMock
            .Setup(c => c.GetEntryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FixCatalogueEntry("Fix.", FixCategory.Retry, 0.0, true));

        var results = new[]
        {
            MakeResult("Timeout",      confidence: 0.8),
            MakeResult("FileNotFound", confidence: 0.9),
        };

        await CreateSut().ExecuteAsync(results);

        _repoMock.Verify(r =>
            r.SaveAsync(It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task GenerateSuggestions_EmptyResults_SavesNothing()
    {
        await CreateSut().ExecuteAsync([]);

        _repoMock.Verify(r =>
            r.SaveAsync(It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateSuggestions_ConfidenceIsClampedBetweenZeroAndOne()
    {
        _catalogueMock
            .Setup(c => c.GetEntryAsync("Transform", It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FixCatalogueEntry("Inspect.", FixCategory.Manual, -0.1, false));

        AiRecommendation? saved = null;
        _repoMock.Setup(r => r.SaveAsync(It.IsAny<AiRecommendation>(), It.IsAny<CancellationToken>()))
            .Callback<AiRecommendation, CancellationToken>((rec, _) => saved = rec)
            .Returns(Task.CompletedTask);

        await CreateSut().ExecuteAsync([MakeResult("Transform", confidence: 0.05)]);

        Assert.NotNull(saved);
        Assert.True(saved!.ConfidenceScore >= 0.0m);
        Assert.True(saved.ConfidenceScore  <= 1.0m);
    }
}
