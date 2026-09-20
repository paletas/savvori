using Microsoft.AspNetCore.Mvc;
using Savvori.WebApi.Modeling;

namespace Savvori.WebApi.Controllers;

/// <summary>Admin API for observing the remote model backend and its work queue.</summary>
[ApiController]
[Route("api/admin/model")]
public class ModelAdminController(IModelStatusService status, ModelJobQueue queue) : ControllerBase
{
    /// <summary>GET /api/admin/model/status — never calls the model; reads breaker state and the queue table.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct = default) =>
        Ok(await status.GetAsync(ct));

    /// <summary>POST /api/admin/model/requeue-dead-letters — gives dead-lettered jobs a fresh attempt budget.</summary>
    [HttpPost("requeue-dead-letters")]
    public async Task<IActionResult> RequeueDeadLetters(CancellationToken ct = default) =>
        Ok(new { Requeued = await queue.RequeueDeadLettersAsync(ct) });
}
