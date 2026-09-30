using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Endpoints de NOTIFICACIONES de la app (clienta y dueña). Solo requieren
/// JWT (sub=AccountId); NO exigen membership. Scoping cross-tenant: las
/// notificaciones se filtran por la cuenta (directas o vía sus Client).
///
/// <c>?audience=buyer|seller</c> separa el historial de clienta del de
/// dueña/administradora: la app lo manda según el papel con el que entró la
/// persona, y una cuenta con ambos papeles nunca mezcla los avisos. Sin el
/// parámetro se devuelve todo (versiones anteriores de la app).
/// </summary>
[ApiController]
[Route("api/me/notifications")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
[SkipTenantResolution]
public class BuyerNotificationController : ControllerBase
{
    private readonly IBuyerNotificationService _service;
    private readonly ICurrentAccount _currentAccount;

    public BuyerNotificationController(
        IBuyerNotificationService service,
        ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// GET /api/me/notifications — historial de notificaciones, ordenado por
    /// fecha descendente. 200 máximo.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<BuyerNotificationDto>>> List(
        [FromQuery] string? audience,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var list = await _service.GetMyNotificationsAsync(
                _currentAccount.AccountId.Value, cancellationToken, audience);
            return Ok(list);
        }
        catch (InvalidNotificationAudienceException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/me/notifications/{id}/read — marca una notificación
    /// como leída. Devuelve 404 si no pertenece a la Account.
    /// </summary>
    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkAsRead(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            await _service.MarkAsReadAsync(
                _currentAccount.AccountId.Value, id, cancellationToken);
            return NoContent();
        }
        catch (NotificationNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/me/notifications/read-all — marca todas las notificaciones
    /// no leídas (del destinatario indicado) como leídas. Devuelve la cantidad.
    /// </summary>
    [HttpPost("read-all")]
    public async Task<ActionResult<object>> MarkAllAsRead(
        [FromQuery] string? audience,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var count = await _service.MarkAllAsReadAsync(
                _currentAccount.AccountId.Value, cancellationToken, audience);
            return Ok(new { updated = count });
        }
        catch (InvalidNotificationAudienceException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/me/notifications/unread-count — contador de no leídas
    /// (lo usa el badge del icono 🔔 en el Home).
    /// </summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> UnreadCount(
        [FromQuery] string? audience,
        CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        try
        {
            var count = await _service.CountUnreadAsync(
                _currentAccount.AccountId.Value, cancellationToken, audience);
            return Ok(count);
        }
        catch (InvalidNotificationAudienceException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
