using Maia.API.Models;
using Maia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireOperator")]   // log-parsing utilities
public class LogParserController(ILogParser logParser) : ControllerBase
{
    [HttpPost("parse")]
    public IActionResult ParseLog([FromBody] LogParseRequest request)
    {
        var lines = logParser.ParseLog(request.LogContent);
        return Ok(lines);
    }

    [HttpPost("extract-first")]
    public IActionResult ExtractFirstError([FromBody] LogParseRequest request)
    {
        var lines = logParser.ParseLog(request.LogContent);
        var error = logParser.ExtractFirstError(lines);
        return Ok(error);
    }
}
