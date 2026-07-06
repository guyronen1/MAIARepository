using Maia.Core.Interfaces.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireOperator")]   // manual classify trigger
public class ClassificationController(IClassifyJobsUseCase classifyUseCase) : ControllerBase
{
    [HttpPost("classify-failures")]
    public async Task<IActionResult> ClassifyFailures(CancellationToken ct)
    {
        var results = await classifyUseCase.ExecuteAsync(ct);
        return Ok(results);
    }
}
