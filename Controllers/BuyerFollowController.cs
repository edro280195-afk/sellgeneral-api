using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// "Seguir tienda" de la compradora. Solo requiere JWT (sub=AccountId); NO
/// exige membership ni negocio activo. El `businessId` viene de la ruta.
/// </summary>
[ApiController]
[Route("api/me/follow")]
[Authorize]
[SkipTenantResolution]
public class BuyerFollowController : ControllerBase
{
    private readonly IBuyerFollowService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerFollowController(IBuyerFollowService service, ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    [HttpGet("{businessId:int}")]
    public async Task<ActionResult<FollowStateDto>> GetState(
        int businessId, CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var state = await _service.GetStateAsync(
            _currentAccount.AccountId.Value, businessId, cancellationToken);
        return Ok(state);
    }

    [HttpPost("{businessId:int}")]
    public async Task<ActionResult<FollowStateDto>> Follow(
        int businessId, [FromBody] FollowPreferencesRequest? preferences,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var state = await _service.FollowAsync(
                _currentAccount.AccountId.Value, businessId, preferences, cancellationToken);
            return Ok(state);
        }
        catch (FollowNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpDelete("{businessId:int}")]
    public async Task<IActionResult> Unfollow(int businessId, CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        await _service.UnfollowAsync(_currentAccount.AccountId.Value, businessId, cancellationToken);
        return NoContent();
    }

    [HttpPut("{businessId:int}/preferences")]
    public async Task<ActionResult<FollowStateDto>> UpdatePreferences(
        int businessId, [FromBody] FollowPreferencesRequest preferences,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var state = await _service.UpdatePreferencesAsync(
                _currentAccount.AccountId.Value, businessId, preferences, cancellationToken);
            return Ok(state);
        }
        catch (FollowNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
