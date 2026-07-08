using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Registro de token de push (FCM) del dispositivo nativo de la compradora.
/// Solo requiere JWT (sub=AccountId); NO exige membership ni negocio activo.
/// </summary>
[ApiController]
[Route("api/me/devices")]
[Authorize]
[SkipTenantResolution]
public class BuyerDeviceController : ControllerBase
{
    private readonly IBuyerDeviceService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerDeviceController(IBuyerDeviceService service, ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    [HttpPost]
    public async Task<IActionResult> Register(
        [FromBody] RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { message = "El token es obligatorio." });
        }

        await _service.RegisterAsync(_currentAccount.AccountId.Value, request, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{token}")]
    public async Task<IActionResult> Unregister(string token, CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        await _service.UnregisterAsync(token, cancellationToken);
        return NoContent();
    }
}
