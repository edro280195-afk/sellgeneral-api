using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/business/meta-live")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
public sealed class MetaLiveProbeController : ControllerBase
{
    private readonly IMetaLiveProbeService _service;

    public MetaLiveProbeController(IMetaLiveProbeService service)
    {
        _service = service;
    }

    [HttpPost("probe")]
    [EnableRateLimiting(SecurityRateLimitPolicies.MetaLiveProbe)]
    [RequestSizeLimit(32 * 1024)]
    public async Task<ActionResult<MetaLiveProbeDto>> Probe(
        [FromBody] MetaLiveProbeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ProbeAsync(
                request.AccessToken,
                cancellationToken));
        }
        catch (MetaLiveProbeException ex)
            when (ex.Failure == MetaLiveProbeFailure.ConfigurationUnavailable)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { message = ex.Message });
        }
        catch (MetaLiveProbeException ex)
            when (ex.Failure is
                  MetaLiveProbeFailure.IdentityNotLinked or
                  MetaLiveProbeFailure.IdentityMismatch)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (MetaLiveProbeException ex)
            when (ex.Failure == MetaLiveProbeFailure.ProviderRejected)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (MetaLiveProbeException ex)
            when (ex.Failure == MetaLiveProbeFailure.ProviderUnavailable)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { message = ex.Message });
        }
    }
}
