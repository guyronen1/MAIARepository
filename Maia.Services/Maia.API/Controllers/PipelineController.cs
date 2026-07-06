using Maia.API.Contracts;
using Maia.Core.Interfaces.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireOperator")]   // legacy directory-scan trigger
public class PipelineController(IDirectoryPipelineUseCase pipeline) : ControllerBase
{
    /// <summary>Scan a directory for log files and run the full classify → suggest → execute pipeline.</summary>
    [HttpPost("run-directory")]
    public async Task<IActionResult> RunDirectoryPipeline(
        [FromBody] PipelineRequest request,
        CancellationToken ct)
    {
        var result = await pipeline.ExecuteAsync(
            request.DirectoryPath,
            request.SearchPattern ?? "*.log",
            request.Recursive     ?? true,
            ct);

        return Ok(result);
    }
}
