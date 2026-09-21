using Microsoft.AspNetCore.Mvc;
using Savvori.WebApi.Modeling;

namespace Savvori.WebApi.Controllers;

/// <summary>Admin API for observing the remote model backend and its work queue.</summary>
[ApiController]
[Route("api/admin/model")]
public class ModelAdminController(
    IModelStatusService status, ModelJobQueue queue, EmbeddingScanner scanner, CandidateGenerator candidates,
    IEmbeddingClient embeddings, CurrentModelState modelState, Microsoft.Extensions.Options.IOptions<ModelOptions> options)
    : ControllerBase
{
    /// <summary>GET /api/admin/model/status — never calls the model; reads breaker state and the queue table.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct = default) =>
        Ok(await status.GetAsync(ct));

    /// <summary>POST /api/admin/model/requeue-dead-letters — gives dead-lettered jobs a fresh attempt budget.</summary>
    [HttpPost("requeue-dead-letters")]
    public async Task<IActionResult> RequeueDeadLetters(CancellationToken ct = default) =>
        Ok(new { Requeued = await queue.RequeueDeadLettersAsync(ct) });

    /// <summary>
    /// POST /api/admin/model/scan — queues embedding jobs for new/changed products now (the hourly job does the same).
    /// Does not wait for the model: it only queues. 409 while the feature flag is off.
    /// </summary>
    [HttpPost("scan")]
    public async Task<IActionResult> Scan(CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return Conflict(new { Message = "Model features are disabled." });
        ModelInfo? info = null;
        try
        {
            info = await embeddings.GetModelInfoAsync(ct);
            modelState.Info = info;
        }
        catch (Exception ex) when (ex is ModelUnavailableException or ModelResponseException)
        {
            // Model unreachable: still queue work (compared by name and text only); it waits for the model.
        }
        return Ok(await scanner.ScanAsync(info, ct));
    }

    /// <summary>
    /// POST /api/admin/model/generate-candidates — regenerates match proposals from stored embeddings now (the nightly
    /// job does the same). Needs no model call. 409 while the feature flag is off.
    /// </summary>
    [HttpPost("generate-candidates")]
    public async Task<IActionResult> GenerateCandidates(CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return Conflict(new { Message = "Model features are disabled." });
        return Ok(await candidates.GenerateAsync(ct));
    }
}
