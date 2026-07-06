using Maia.Application.Classification;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Maia.Tests.Unit;

public class ClassifyJobsUseCaseTests
{
    private readonly Mock<IJobRepository>          _jobsMock       = new();
    private readonly Mock<IClassificationStrategy> _strategyMock   = new();

    private ClassifyJobsUseCase CreateSut() =>
        new(_jobsMock.Object, _strategyMock.Object,
            NullLogger<ClassifyJobsUseCase>.Instance);

    private JobFailure MakeJob(int id, string logContent)
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, logContent);
        // Strategy classifies against job.ErrorMessage (the captured line), not the file —
        // tests pass logContent in both so the original behavior assertions still hold.
        return new JobFailure
        {
            FailureId = id, JobId = id, JobTypeId = 1,
            Status = JobStatus.Failed, SourceLogPath = file,
            ErrorMessage = logContent,
        };
    }

    private void SetupStrategy(ClassificationResult? result)
        => _strategyMock.Setup(s => s.ClassifyAsync(
                It.IsAny<JobFailure>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Classify_FileNotFoundException_ReturnsFileNotFound()
    {
        var job = MakeJob(1, "System.IO.FileNotFoundException: file.txt not found");
        _jobsMock.Setup(r => r.GetUnclassifiedAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync([job]);
        SetupStrategy(new ClassificationResult
        {
            FailureId = 1, JobId = 1, JobTypeId = 1,
            ErrorTypeId = 1, ErrorTypeCode = "FileNotFound",
            RawError = "FileNotFoundException", Confidence = 0.95
        });

        var results = (await CreateSut().ExecuteAsync()).ToList();

        Assert.Single(results);
        Assert.Equal("FileNotFound", results[0].ErrorTypeCode);
        Assert.True(results[0].Confidence >= 0.9);
    }

    [Fact]
    public async Task Classify_SqlException_ReturnsDbConnection()
    {
        var job = MakeJob(2, "SqlException: Login failed for user 'sa'");
        _jobsMock.Setup(r => r.GetUnclassifiedAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync([job]);
        SetupStrategy(new ClassificationResult
        {
            FailureId = 2, JobId = 2, JobTypeId = 1,
            ErrorTypeId = 2, ErrorTypeCode = "DbConnection",
            RawError = "SqlException", Confidence = 0.95
        });

        var results = (await CreateSut().ExecuteAsync()).ToList();

        Assert.Single(results);
        Assert.Equal("DbConnection", results[0].ErrorTypeCode);
    }

    [Fact]
    public async Task Classify_TimeoutExpired_ReturnsTimeout()
    {
        var job = MakeJob(3, "Timeout expired. The timeout period elapsed");
        _jobsMock.Setup(r => r.GetUnclassifiedAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync([job]);
        SetupStrategy(new ClassificationResult
        {
            FailureId = 3, JobId = 3, JobTypeId = 1,
            ErrorTypeId = 3, ErrorTypeCode = "Timeout",
            RawError = "Timeout expired", Confidence = 0.88
        });

        var results = (await CreateSut().ExecuteAsync()).ToList();

        Assert.Single(results);
        Assert.Equal("Timeout", results[0].ErrorTypeCode);
    }

    [Fact]
    public async Task Classify_DtsOledbError_ReturnsTransform()
    {
        var job = MakeJob(4, "DTS_E_OLEDBERROR: An OLE DB error occurred");
        _jobsMock.Setup(r => r.GetUnclassifiedAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync([job]);
        SetupStrategy(new ClassificationResult
        {
            FailureId = 4, JobId = 4, JobTypeId = 1,
            ErrorTypeId = 4, ErrorTypeCode = "Transform",
            RawError = "DTS_E_OLEDBERROR", Confidence = 0.85
        });

        var results = (await CreateSut().ExecuteAsync()).ToList();

        Assert.Single(results);
        Assert.Equal("Transform", results[0].ErrorTypeCode);
    }

    [Fact]
    public async Task Classify_UnknownError_ReturnsUnknownWithLowConfidence()
    {
        var job = MakeJob(5, "ERROR: something unexpected happened");
        _jobsMock.Setup(r => r.GetUnclassifiedAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync([job]);
        SetupStrategy(new ClassificationResult
        {
            FailureId = 5, JobId = 5, JobTypeId = 1,
            ErrorTypeId = 6, ErrorTypeCode = "Unknown",
            RawError = "ERROR:", Confidence = 0.50
        });

        var results = (await CreateSut().ExecuteAsync()).ToList();

        Assert.Single(results);
        Assert.Equal("Unknown", results[0].ErrorTypeCode);
        Assert.True(results[0].Confidence < 0.6);
    }

    [Fact]
    public async Task Classify_CleanLog_ReturnsNoResults()
    {
        var job = MakeJob(6, "Starting job\nProcessing rows\nCompleted successfully");
        _jobsMock.Setup(r => r.GetUnclassifiedAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync([job]);
        SetupStrategy(null);

        var results = (await CreateSut().ExecuteAsync()).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public async Task Classify_MissingLogFile_ReturnsNoResults()
    {
        var job = new JobFailure
        {
            FailureId = 7, JobId = 7, JobTypeId = 1,
            Status = JobStatus.Failed, SourceLogPath = "/nonexistent/job.log"
        };
        _jobsMock.Setup(r => r.GetUnclassifiedAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync([job]);
        // No need to mock the log reader anymore — classifier reads job.ErrorMessage directly.
        SetupStrategy(null);

        var results = (await CreateSut().ExecuteAsync()).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public async Task Classify_MultipleFailedJobs_ClassifiesAll()
    {
        var job1 = MakeJob(10, "SqlException: Login failed");
        var job2 = MakeJob(11, "FileNotFoundException: missing.txt");

        _jobsMock.Setup(r => r.GetUnclassifiedAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync([job1, job2]);

        _strategyMock
            .SetupSequence(s => s.ClassifyAsync(
                It.IsAny<JobFailure>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult
            {
                FailureId = 10, JobId = 10, JobTypeId = 1,
                ErrorTypeId = 2, ErrorTypeCode = "DbConnection",
                RawError = "SqlException", Confidence = 0.95
            })
            .ReturnsAsync(new ClassificationResult
            {
                FailureId = 11, JobId = 11, JobTypeId = 1,
                ErrorTypeId = 1, ErrorTypeCode = "FileNotFound",
                RawError = "FileNotFoundException", Confidence = 0.95
            });

        var results = (await CreateSut().ExecuteAsync()).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.ErrorTypeCode == "DbConnection");
        Assert.Contains(results, r => r.ErrorTypeCode == "FileNotFound");
    }
}
