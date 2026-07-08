using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Lado vendedora: marcar "estoy en vivo ahora mismo" (tiempo real, distinto
/// del pipeline post-hoc de <see cref="LiveSession"/>). Tenant-scoped,
/// gateado por el plan (Pro+).
/// </summary>
[ApiController]
[Route("api/business/live-announcements")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
public class LiveAnnouncementController : ControllerBase
{
    private readonly ILiveAnnouncementService _service;

    public LiveAnnouncementController(ILiveAnnouncementService service)
    {
        _service = service;
    }

    [HttpPost]
    [RequiresFeature(Feature.LivePush)]
    public async Task<ActionResult<LiveAnnouncementDto>> Start(
        [FromBody] StartLiveAnnouncementRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _service.StartAsync(request, cancellationToken);
            return Ok(dto);
        }
        catch (LiveAnnouncementAlreadyActiveException ex)
        {
            return Conflict(new { message = ex.Message, active = ex.Active });
        }
    }

    [HttpPost("{id:int}/end")]
    [RequiresFeature(Feature.LivePush)]
    public async Task<IActionResult> End(int id, CancellationToken cancellationToken)
    {
        await _service.EndAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("active")]
    public async Task<ActionResult<LiveAnnouncementDto>> GetActive(CancellationToken cancellationToken)
    {
        var active = await _service.GetActiveAsync(cancellationToken);
        return active is null ? NoContent() : Ok(active);
    }
}
