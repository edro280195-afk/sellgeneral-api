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
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
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

    /// <summary>
    /// POST /api/me/devices/test-push: manda una notificacion de prueba a los
    /// dispositivos de la propia cuenta y devuelve el diagnostico. Sirve para
    /// comprobar de punta a punta el token FCM y la credencial de Firebase Admin.
    /// </summary>
    [HttpPost("test-push")]
    public async Task<ActionResult<DevicePushTestDto>> TestPush(CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesion invalida." });
        }

        return Ok(await _service.SendTestPushAsync(_currentAccount.AccountId.Value, cancellationToken));
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
