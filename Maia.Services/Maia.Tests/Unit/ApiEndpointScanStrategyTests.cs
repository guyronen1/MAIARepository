using System.Net;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Interfaces.UseCases;
using Maia.Core.Results;
using Maia.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Unit tests for ApiEndpointScanStrategy.
/// The strategy has no per-item iteration loop — it does exactly one HTTP GET per
/// ScanAsync call, so there is no mechanism to orphan earlier work (unlike FS/DB/FileContent
/// strategies, which iterate over files or rules). The resilience contract here is
/// simply: a thrown HTTP exception is caught and converted to a failure.
/// </summary>
public sealed class ApiEndpointScanStrategyTests
{
    private const string TestUrl = "http://api.example.test/status";

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly List<JobFailure>           Saved      = new();
        public readonly List<JobFailure>           Classified = new();
        private readonly Mock<HttpMessageHandler>  _handler   = new();

        /// <summary>
        /// Wire the mock HTTP handler to return a specific response.
        /// Must be called before <see cref="Build"/> when testing HTTP paths.
        /// </summary>
        public Harness Returns(HttpStatusCode code, string body = "")
        {
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(code)
                {
                    Content = new StringContent(body),
                });
            return this;
        }

        /// <summary>Wire the mock HTTP handler to throw on any request.</summary>
        public Harness Throws(Exception ex)
        {
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(ex);
            return this;
        }

        public ApiEndpointScanStrategy Build()
        {
            var client = new HttpClient(_handler.Object);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

            var next   = 1;
            var repo   = new Mock<IJobRepository>();
            repo.Setup(r => r.SaveAsync(It.IsAny<JobFailure>(), It.IsAny<CancellationToken>()))
                .Returns((JobFailure f, CancellationToken _) =>
                {
                    f.FailureId = next++;
                    Saved.Add(f);
                    return Task.FromResult(f);
                });

            var classify = new Mock<IClassifyJobsUseCase>();
            classify.Setup(c => c.ExecuteAsync(It.IsAny<IEnumerable<JobFailure>>(), It.IsAny<CancellationToken>()))
                    .Returns((IEnumerable<JobFailure> fs, CancellationToken _) =>
                    {
                        Classified.AddRange(fs);
                        return Task.FromResult((IReadOnlyList<ClassificationResult>)Array.Empty<ClassificationResult>());
                    });

            var suggest = new Mock<IGenerateSuggestionsUseCase>();
            suggest.Setup(s => s.ExecuteAsync(It.IsAny<IEnumerable<ClassificationResult>>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

            return new ApiEndpointScanStrategy(
                factory.Object, repo.Object, classify.Object, suggest.Object,
                NullLogger<ApiEndpointScanStrategy>.Instance);
        }
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static (MonitoredJob job, ScanSource source) JobAndSource(string? url = TestUrl)
    {
        var job = new MonitoredJob { MonitoredJobId = 42, JobTypeId = 3, Name = "ApiJob" };
        var source = new ScanSource
        {
            ScanSourceId   = 7,
            MonitoredJobId = 42,
            Name           = "HealthCheck",
            LogSourceUrl   = url,
        };
        return (job, source);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void ScanType_IsApiEndpoint()
    {
        var strategy = new Harness().Returns(HttpStatusCode.OK).Build();
        Assert.Equal(ScanType.ApiEndpoint, strategy.ScanType);
    }

    [Fact]
    public async Task NullLogSourceUrl_Throws()
    {
        var strategy = new Harness().Build();
        var (job, source) = JobAndSource(url: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.ScanAsync(job, source));
    }

    [Fact]
    public async Task SuccessResponse_CleanBody_NoFailure()
    {
        var h = new Harness().Returns(HttpStatusCode.OK, "all good");
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
        Assert.Empty(h.Saved);
        Assert.Empty(h.Classified);
    }

    [Fact]
    public async Task ScanResult_Detail_ContainsUrl()
    {
        var h = new Harness().Returns(HttpStatusCode.OK, "healthy");
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal($"URL: {TestUrl}", result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "Server error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "down")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    public async Task Non2xxStatus_CreatesFailure(HttpStatusCode code, string body)
    {
        var h = new Harness().Returns(code, body);
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal(1, result.FailuresDetected);
        var f = Assert.Single(h.Saved);
        Assert.Contains(code.ToString(), f.ErrorMessage);
    }

    [Theory]
    [InlineData("status: error in processing")]   // 'error' — case-insensitive
    [InlineData("NullReferenceException thrown")]  // 'exception'
    [InlineData("job failed to complete")]         // 'failed'
    [InlineData("ERROR: disk full")]               // uppercase variant
    public async Task BodyKeyword_CreatesFailure(string body)
    {
        var h = new Harness().Returns(HttpStatusCode.OK, body);
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal(1, result.FailuresDetected);
        Assert.Single(h.Saved);
    }

    [Fact]
    public async Task HttpThrows_CreatesFailure_StatusUnreachable()
    {
        var ex = new HttpRequestException("connection timed out");
        var h = new Harness().Throws(ex);
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal(1, result.FailuresDetected);
        var f = Assert.Single(h.Saved);
        Assert.Contains("Unreachable", f.ErrorMessage);
        Assert.Contains("connection timed out", f.ErrorMessage);
    }

    [Fact]
    public async Task BodyOver500Chars_SnippetTruncatedTo500()
    {
        var longBody = "error: " + new string('x', 600);
        var h = new Harness().Returns(HttpStatusCode.OK, longBody);
        var (job, source) = JobAndSource();

        await h.Build().ScanAsync(job, source);

        var f = Assert.Single(h.Saved);
        // ErrorMessage = "API check failed: status=OK, body=<snippet>"
        // The snippet portion is capped at 500 chars.
        var bodyPart = f.ErrorMessage["API check failed: status=OK, body=".Length..];
        Assert.Equal(500, bodyPart.Length);
    }

    [Fact]
    public async Task FailureFields_PopulatedCorrectly()
    {
        var h = new Harness().Returns(HttpStatusCode.InternalServerError, "error");
        var (job, source) = JobAndSource();

        await h.Build().ScanAsync(job, source);

        var f = Assert.Single(h.Saved);
        Assert.Equal(job.MonitoredJobId,  f.MonitoredJobId);
        Assert.Equal(job.JobTypeId,        f.JobTypeId);
        Assert.Equal(source.ScanSourceId,  f.ScanSourceId);
        Assert.Equal("ApiEndpointCheck",   f.StepName);
        Assert.Equal(TestUrl,              f.SourceId);
        Assert.Equal(TestUrl,              f.SourceLogPath);
        Assert.Equal(JobStatus.Failed,     f.Status);
    }

    [Fact]
    public async Task Failure_ClassifyAndSuggest_AreCalled()
    {
        var h = new Harness().Returns(HttpStatusCode.InternalServerError, "down");
        var (job, source) = JobAndSource();

        await h.Build().ScanAsync(job, source);

        // classify must receive the saved failure
        Assert.Single(h.Classified);
        Assert.Equal(h.Saved[0].FailureId, h.Classified[0].FailureId);
    }

    [Fact]
    public async Task Success_ClassifyAndSuggest_NotCalled()
    {
        var h = new Harness().Returns(HttpStatusCode.OK, "healthy");
        var (job, source) = JobAndSource();

        await h.Build().ScanAsync(job, source);

        Assert.Empty(h.Classified);
    }

    [Fact]
    public async Task ScanResult_ScanType_IsApiEndpoint()
    {
        var h = new Harness().Returns(HttpStatusCode.OK, "ok");
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal(ScanType.ApiEndpoint, result.ScanType);
    }

    [Fact]
    public async Task ScanResult_JobName_MatchesJob()
    {
        var h = new Harness().Returns(HttpStatusCode.OK, "ok");
        var (job, source) = JobAndSource();

        var result = await h.Build().ScanAsync(job, source);

        Assert.Equal(job.Name, result.JobName);
    }
}
