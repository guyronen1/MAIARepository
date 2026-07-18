using Maia.API.Contracts;
using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

/// <summary>
/// MonitoredJob CRUD. Split out of ConfigController; see <see cref="ConfigControllerBase"/>.
/// A job is a pure identity container — scan config lives on its ScanSources
/// (see ScanSourcesConfigController), not here.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize(Policy = "RequireOperator")]
public class MonitoredJobsConfigController(
    IMonitoredJobRepository               jobRepo,
    IAuditRepository                      audit,
    ICurrentUserAccessor                  currentUser,
    ILogger<MonitoredJobsConfigController> logger)
    : ConfigControllerBase(audit, currentUser, logger)
{
    [HttpGet("monitored-jobs")]
    public async Task<IActionResult> GetAllJobs(CancellationToken ct)
    {
        var jobs = await jobRepo.GetAllWithRulesAsync(ct);
        return Ok(jobs.Select(MonitoredJobDto.From));
    }

    /// <summary>Full operational picture of one job for the dedicated config screen
    /// (Tier 2.5 d2): active sources with their active rules, classification rules,
    /// fix policies. One round-trip. (Lease state isn't included — the config screen
    /// doesn't render it; the dashboard polls worker-status for that.)</summary>
    [HttpGet("monitored-jobs/{id:int}")]
    public async Task<IActionResult> GetJob(int id, CancellationToken ct)
    {
        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null) return NotFound();
        return Ok(MonitoredJobDto.From(job));
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPost("monitored-jobs")]
    public async Task<IActionResult> CreateJob([FromBody] UpsertMonitoredJobRequest req, CancellationToken ct)
    {
        var job = new MonitoredJob
        {
            Name                   = req.Name,
            DisplayName            = req.DisplayName,
            JobTypeId              = req.JobTypeId,
            PollingIntervalSeconds = req.PollingIntervalSeconds,
            IsActive               = req.IsActive,
            Description            = req.Description,
            CreatedAt              = DateTime.Now,
        };
        var saved = await jobRepo.SaveAsync(job, ct);

        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   saved.MonitoredJobId.ToString(),
            eventType:  "MonitoredJobCreated",
            actor:      Actor,
            detail:     $"Created MonitoredJob '{saved.Name}' (JobTypeId={saved.JobTypeId}, PollingIntervalSeconds={saved.PollingIntervalSeconds}, IsActive={FormatValue(saved.IsActive)})",
            ct: ct);

        return Ok(new { saved.MonitoredJobId });
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpPut("monitored-jobs/{id:int}")]
    public async Task<IActionResult> UpdateJob(int id, [FromBody] UpsertMonitoredJobRequest req, CancellationToken ct)
    {
        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null) return NotFound();

        // Snapshot before mutation for the audit diff. Tier 2.5 Option 1: a job is pure
        // identity — scan config (ScanType/folder/pattern/connection/url) lives on its
        // ScanSources and is NOT edited here.
        var beforeName           = job.Name;
        var beforeDisplayName    = job.DisplayName;
        var beforeJobTypeId      = job.JobTypeId;
        var beforePollingInterval= job.PollingIntervalSeconds;
        var beforeIsActive       = job.IsActive;
        var beforeDescription    = job.Description;

        job.Name                   = req.Name;
        job.DisplayName            = req.DisplayName;
        job.JobTypeId              = req.JobTypeId;
        job.PollingIntervalSeconds = req.PollingIntervalSeconds;
        job.IsActive               = req.IsActive;
        job.Description            = req.Description;

        await jobRepo.UpdateAsync(job, ct);

        var diff = BuildDiff(
            ("Name",                   beforeName,            job.Name),
            ("DisplayName",            beforeDisplayName,     job.DisplayName),
            ("JobTypeId",              beforeJobTypeId,       job.JobTypeId),
            ("PollingIntervalSeconds", beforePollingInterval, job.PollingIntervalSeconds),
            ("IsActive",               beforeIsActive,        job.IsActive),
            ("Description",            beforeDescription,     job.Description));
        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   id.ToString(),
            eventType:  "MonitoredJobUpdated",
            actor:      Actor,
            detail:     diff.Length > 0 ? diff : "No changes",
            ct: ct);

        return NoContent();
    }

    [Authorize(Policy = "RequireAdmin")]
    [HttpDelete("monitored-jobs/{id:int}")]
    public async Task<IActionResult> DeleteJob(int id, CancellationToken ct)
    {
        // Snapshot name for the audit row before the entity is gone.
        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null) return NotFound();
        var jobName = job.Name;

        await jobRepo.DeleteAsync(id, ct);

        await WriteAuditAsync(
            entityType: "MonitoredJob",
            entityId:   id.ToString(),
            eventType:  "MonitoredJobDeleted",
            actor:      Actor,
            detail:     $"Deleted MonitoredJob {id} ('{jobName}')",
            ct: ct);

        return NoContent();
    }
}
