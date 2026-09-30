using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
        // Mismos límites que las columnas (512 / 20): un valor más largo fallaba en la base con un 500.
        if (request.Token.Trim().Length > 512 || request.Platform is { Length: > 20 })
        {
            return BadRequest(new { message = "El token o la plataforma no son válidos." });
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

    /// <summary>
    /// DELETE /api/me/devices/{token}: quita el token de push de este dispositivo
    /// (al cerrar sesión). Es anónimo A PROPÓSITO: el token FCM es un secreto del
    /// propio dispositivo (~160 caracteres aleatorios), y quien lo conoce ya puede
    /// recibir sus avisos. Exigir sesión aquí hacía que el logout dejara el token
    /// registrado siempre que la sesión ya hubiera expirado o se hubiera borrado
    /// antes de la llamada, y entonces los avisos de la cuenta que salió seguían
    /// llegando a ese teléfono.
    /// </summary>
    [HttpDelete("{token}")]
    [AllowAnonymous]
    [EnableRateLimiting(SecurityRateLimitPolicies.PushSubscribe)]
    public async Task<IActionResult> Unregister(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
        {
            return BadRequest(new { message = "El token no es válido." });
        }

        await _service.UnregisterAsync(token, cancellationToken);
        return NoContent();
    }
}
