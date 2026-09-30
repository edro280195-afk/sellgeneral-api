using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WebPush;
using System.Text.Json;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PushController : ControllerBase
{
    private const string BusinessHeader = "X-Business-Id";

    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IFcmService _fcm;
    private readonly ICurrentAccount _currentAccount;

    public PushController(AppDbContext db, IConfiguration config, IFcmService fcm, ICurrentAccount currentAccount)
    {
        _db = db;
        _config = config;
        _fcm = fcm;
        _currentAccount = currentAccount;
    }

    // ═══════════════════════════════════════════
    //  FCM — ANDROID NATIVE (CHOFERES)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Registra o actualiza un token FCM de dispositivo Android.
    /// Llamar al iniciar la app y al refrescar el token.
    ///
    /// Es anónimo (el chofer no tiene cuenta), así que NO se confía en lo que diga el
    /// cuerpo: el token de ruta debe existir y fija el negocio del dispositivo, y el rol
    /// "admin" exige sesión de dueña/administradora del negocio.
    /// </summary>
    [HttpPost("subscribe-fcm")]
    [AllowAnonymous]
    [EnableRateLimiting(SecurityRateLimitPolicies.PushSubscribe)]
    public async Task<IActionResult> SubscribeFcm([FromBody] FcmSubscribeRequest req, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(req.FcmToken))
            return BadRequest("FcmToken requerido.");
        if (req.FcmToken.Length > 4096)
            return BadRequest("FcmToken demasiado largo.");
        if (!IsAllowedRole(req.Role, "driver", "admin"))
            return BadRequest("Role invalido.");
        if (req.DriverRouteToken is { Length: > 128 })
            return BadRequest("DriverRouteToken demasiado largo.");

        var role = NormalizeRole(req.Role, "driver");
        var businessId = 0; // 0 = sin resolver (se estampa el negocio por defecto, como antes)
        string? driverRouteToken = null;

        if (role == "admin")
        {
            var admin = await ResolveAdminBusinessAsync(cancellationToken);
            if (admin.Error is not null) return admin.Error;
            businessId = admin.BusinessId;
        }
        else if (!string.IsNullOrWhiteSpace(req.DriverRouteToken))
        {
            // Solo se aceptan tokens de rutas que existen; uno inventado se descarta
            // (el dispositivo igual queda registrado como chofer, sin ruta).
            var route = await _db.DeliveryRoutes.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.DriverToken == req.DriverRouteToken)
                .Select(r => new { r.BusinessId })
                .FirstOrDefaultAsync(cancellationToken);
            if (route is not null)
            {
                driverRouteToken = req.DriverRouteToken;
                businessId = route.BusinessId;
            }
        }

        var existing = await _db.FcmTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Token == req.FcmToken, cancellationToken);

        if (existing == null)
        {
            _db.FcmTokens.Add(new FcmToken
            {
                BusinessId = businessId,
                Token = req.FcmToken,
                Role = role,
                DriverRouteToken = driverRouteToken,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            // Un dispositivo de administración no se convierte en chofer por una petición anónima.
            if (existing.Role == "admin" && role != "admin")
                return Ok(new { success = false });

            if (!string.IsNullOrEmpty(req.Role)) existing.Role = role;
            if (driverRouteToken != null)
            {
                existing.DriverRouteToken = driverRouteToken;
                existing.BusinessId = businessId;
            }
            if (role == "admin") existing.BusinessId = businessId;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Elimina un token FCM (al hacer logout en la app).
    /// </summary>
    [HttpDelete("unsubscribe-fcm")]
    [AllowAnonymous]
    [EnableRateLimiting(SecurityRateLimitPolicies.PushSubscribe)]
    public async Task<IActionResult> UnsubscribeFcm([FromQuery] string fcmToken)
    {
        if (string.IsNullOrWhiteSpace(fcmToken) || fcmToken.Length > 4096)
            return BadRequest("FcmToken invalido.");

        var existing = await _db.FcmTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Token == fcmToken);
        if (existing != null)
        {
            _db.FcmTokens.Remove(existing);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    // ═══════════════════════════════════════════
    //  SUSCRIPCIÓN WEB PUSH (Role validado)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Suscribe un navegador a Web Push. Es anónimo (la clienta y el chofer entran por
    /// enlace), así que el rol NO se toma por bueno: cada rol exige la prueba que le
    /// corresponde y el negocio y la clienta se derivan del recurso validado, nunca del
    /// cuerpo:
    ///   • "admin"  → sesión (JWT) de dueña/administradora del negocio.
    ///   • "client" → token del pedido (<see cref="PushSubscriptionRequest.OrderToken"/>);
    ///                la clienta y el negocio salen del pedido.
    ///   • "driver" → token de una ruta existente; el negocio sale de la ruta.
    /// </summary>
    [HttpPost("subscribe")]
    [AllowAnonymous]
    [EnableRateLimiting(SecurityRateLimitPolicies.PushSubscribe)]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest req, CancellationToken cancellationToken)
    {
        var validationError = ValidateSubscription(req);
        if (validationError is not null) return validationError;

        var role = NormalizeRole(req.Role, "client");
        var owner = await ResolveSubscriptionOwnerAsync(role, req, cancellationToken);
        if (owner.Error is not null) return owner.Error;

        var existing = await _db.PushSubscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint, cancellationToken);

        if (existing == null)
        {
            _db.PushSubscriptions.Add(new PushSubscriptionModel
            {
                BusinessId = owner.BusinessId,
                Endpoint = req.Endpoint,
                P256dh = req.Keys.P256dh,
                Auth = req.Keys.Auth,
                Role = role,
                ClientId = owner.ClientId,
                DriverRouteToken = owner.DriverRouteToken,
                LastUsedAt = DateTime.UtcNow
            });
        }
        else
        {
            // Un navegador que ya quedó como administración no pasa a ser de clienta o
            // chofer por una petición anónima (p. ej. la dueña abriendo un enlace de pedido).
            if (existing.Role == "admin" && role != "admin")
                return Ok(new { success = false, reason = "admin_device" });

            existing.P256dh = req.Keys.P256dh;
            existing.Auth = req.Keys.Auth;
            existing.LastUsedAt = DateTime.UtcNow;

            // Rol, negocio y dueño se re-derivan SIEMPRE del recurso validado.
            existing.BusinessId = owner.BusinessId;
            existing.Role = role;
            existing.ClientId = owner.ClientId;
            existing.DriverRouteToken = owner.DriverRouteToken;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpDelete("unsubscribe")]
    [AllowAnonymous]
    [EnableRateLimiting(SecurityRateLimitPolicies.PushSubscribe)]
    public async Task<IActionResult> Unsubscribe([FromQuery] string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048)
            return BadRequest("Endpoint invalido.");

        var sub = await _db.PushSubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Endpoint == endpoint);
        if (sub != null)
        {
            _db.PushSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    // ═══════════════════════════════════════════
    //  ENVÍO DIRIGIDO POR ROL
    // ═══════════════════════════════════════════

    /// <summary>
    /// Envía push a una clienta específica por su ClientId.
    /// Útil para: chat del chofer→clienta, cambios de estado, proximidad.
    /// </summary>
    [HttpPost("send/client/{clientId}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> SendToClient(int clientId, [FromBody] NotificationPayload payload)
    {
        var validationError = ValidateNotificationPayload(payload);
        if (validationError is not null) return validationError;

        var subs = await _db.PushSubscriptions
            .Where(s => s.Role == "client" && s.ClientId == clientId)
            .ToListAsync();

        var result = await SendToSubscriptions(subs, payload);
        return Ok(result);
    }

    /// <summary>
    /// Envía push al chofer de una ruta específica por su DriverRouteToken.
    /// Útil para: chat del admin→chofer, chat de clienta→chofer.
    /// </summary>
    [HttpPost("send/driver/{routeToken}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> SendToDriver(string routeToken, [FromBody] NotificationPayload payload)
    {
        var validationError = ValidateNotificationPayload(payload);
        if (validationError is not null) return validationError;

        var subs = await _db.PushSubscriptions
            .Where(s => s.Role == "driver" && s.DriverRouteToken == routeToken)
            .ToListAsync();

        var result = await SendToSubscriptions(subs, payload);
        return Ok(result);
    }

    /// <summary>
    /// Envía push a TODOS los admins.
    /// Útil para: chat del chofer→admin, alertas de entregas fallidas.
    /// </summary>
    [HttpPost("send/admins")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> SendToAdmins([FromBody] NotificationPayload payload)
    {
        var validationError = ValidateNotificationPayload(payload);
        if (validationError is not null) return validationError;

        var subs = await _db.PushSubscriptions
            .Where(s => s.Role == "admin")
            .ToListAsync();

        var result = await SendToSubscriptions(subs, payload);
        return Ok(result);
    }

    /// <summary>
    /// Endpoint de prueba de la administración: envía SOLO a las suscripciones de
    /// administración de este negocio. Una prueba nunca debe llegar a clientas ni a choferes.
    /// </summary>
    [HttpPost("test")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> TestNotification([FromBody] NotificationPayload payload)
    {
        var validationError = ValidateNotificationPayload(payload);
        if (validationError is not null) return validationError;

        var subs = await _db.PushSubscriptions.Where(s => s.Role == "admin").ToListAsync();
        if (!subs.Any()) return NotFound("No hay suscripciones activas.");

        var result = await SendToSubscriptions(subs, payload);
        return Ok(result);
    }

    // ═══════════════════════════════════════════
    //  VALIDACIÓN DEL DUEÑO DE LA SUSCRIPCIÓN
    // ═══════════════════════════════════════════

    private sealed record SubscriptionOwner(
        int BusinessId,
        int? ClientId = null,
        string? DriverRouteToken = null,
        IActionResult? Error = null);

    private async Task<SubscriptionOwner> ResolveSubscriptionOwnerAsync(
        string role, PushSubscriptionRequest req, CancellationToken cancellationToken)
    {
        switch (role)
        {
            case "admin":
                {
                    var admin = await ResolveAdminBusinessAsync(cancellationToken);
                    return admin.Error is not null
                        ? new SubscriptionOwner(0, Error: admin.Error)
                        : new SubscriptionOwner(admin.BusinessId);
                }

            case "driver":
                {
                    var routeToken = req.DriverRouteToken?.Trim();
                    if (string.IsNullOrEmpty(routeToken))
                        return new SubscriptionOwner(0, Error: BadRequest("Falta el token de la ruta del chofer."));

                    var route = await _db.DeliveryRoutes.IgnoreQueryFilters().AsNoTracking()
                        .Where(r => r.DriverToken == routeToken)
                        .Select(r => new { r.BusinessId })
                        .FirstOrDefaultAsync(cancellationToken);
                    if (route is null)
                        return new SubscriptionOwner(0, Error: BadRequest("La ruta no existe."));

                    return new SubscriptionOwner(route.BusinessId, DriverRouteToken: routeToken);
                }

            default: // "client"
                {
                    var orderToken = req.OrderToken?.Trim();
                    if (string.IsNullOrEmpty(orderToken))
                        return new SubscriptionOwner(0, Error: BadRequest("Falta el enlace del pedido para activar los avisos."));

                    // La clienta y el negocio salen del PEDIDO, no del cuerpo: nadie puede
                    // suscribirse a los avisos de otra clienta diciendo su ClientId.
                    var order = await _db.Orders.IgnoreQueryFilters().AsNoTracking()
                        .Where(o => o.AccessToken == orderToken)
                        .Select(o => new { o.ClientId, o.BusinessId })
                        .FirstOrDefaultAsync(cancellationToken);
                    if (order is null)
                        return new SubscriptionOwner(0, Error: BadRequest("El enlace del pedido no es válido."));

                    return new SubscriptionOwner(order.BusinessId, ClientId: order.ClientId);
                }
        }
    }

    /// <summary>
    /// Negocio del que la persona autenticada es dueña/administradora. Mismas reglas
    /// que el middleware de tenant: con un solo negocio se usa ese; con varios
    /// <c>X-Business-Id</c> es obligatorio y debe ser uno de los suyos.
    /// </summary>
    private async Task<(int BusinessId, IActionResult? Error)> ResolveAdminBusinessAsync(CancellationToken cancellationToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is not int accountId)
        {
            return (0, Unauthorized(new { message = "Inicia sesión como administradora para activar los avisos." }));
        }

        var businessIds = await _db.Memberships.AsNoTracking()
            .Where(m => m.AccountId == accountId
                        && (m.Role == MembershipRole.Owner || m.Role == MembershipRole.Admin)
                        && m.Business!.IsActive)
            .Select(m => m.BusinessId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (businessIds.Count == 0)
        {
            return (0, StatusCode(StatusCodes.Status403Forbidden, new { message = "No eres dueña ni administradora de ningún negocio." }));
        }

        var requested = Request.Headers.TryGetValue(BusinessHeader, out var header)
            ? header.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(requested))
        {
            return businessIds.Count == 1
                ? (businessIds[0], null)
                : (0, BadRequest(new { message = "X-Business-Id es obligatorio para cuentas con más de un negocio." }));
        }

        if (!int.TryParse(requested, out var businessId))
        {
            return (0, BadRequest(new { message = "X-Business-Id inválido." }));
        }

        return businessIds.Contains(businessId)
            ? (businessId, null)
            : (0, StatusCode(StatusCodes.Status403Forbidden, new { message = "No tienes acceso a ese negocio." }));
    }

    // ═══════════════════════════════════════════
    //  HELPER INTERNO DE ENVÍO
    // ═══════════════════════════════════════════

    private async Task<object> SendToSubscriptions(List<PushSubscriptionModel> subs, NotificationPayload payload)
    {
        if (!subs.Any())
            return new { success = 0, failed = 0, message = "No hay suscripciones para este destino." };

        var subject = _config["VapidDetails:Subject"] ?? "mailto:info@regibazar.com";
        var publicKey = _config["VapidDetails:PublicKey"];
        var privateKey = _config["VapidDetails:PrivateKey"];

        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(privateKey))
            return new { success = 0, failed = 0, message = "VAPID Keys no configuradas." };

        var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
        var webPushClient = new WebPushClient();

        int successCount = 0;
        int failCount = 0;

        foreach (var sub in subs)
        {
            try
            {
                var pushSubscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                var jsonPayload = JsonSerializer.Serialize(new
                {
                    notification = new
                    {
                        title = payload.Title,
                        body = payload.Body,
                        icon = payload.Icon ?? "/assets/icons/icon-192x192.png",
                        badge = "/assets/icons/icon-72x72.png",
                        vibrate = new[] { 200, 100, 200 },
                        tag = payload.Tag,
                        data = new
                        {
                            url = payload.Url ?? "/",
                            type = payload.Type
                        }
                    }
                });

                await webPushClient.SendNotificationAsync(pushSubscription, jsonPayload, vapidDetails);
                sub.LastUsedAt = DateTime.UtcNow;
                successCount++;
            }
            catch (WebPushException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Gone ||
                    ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Suscripción expirada → eliminar
                    _db.PushSubscriptions.Remove(sub);
                }
                failCount++;
            }
            catch
            {
                failCount++;
            }
        }

        await _db.SaveChangesAsync();
        return new { success = successCount, failed = failCount };
    }

    private BadRequestObjectResult? ValidateSubscription(PushSubscriptionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint) || req.Endpoint.Length > 2048)
            return BadRequest("Endpoint requerido o demasiado largo.");
        if (req.Keys is null ||
            string.IsNullOrWhiteSpace(req.Keys.P256dh) ||
            string.IsNullOrWhiteSpace(req.Keys.Auth) ||
            req.Keys.P256dh.Length > 512 ||
            req.Keys.Auth.Length > 512)
            return BadRequest("Llaves push invalidas.");
        if (!IsAllowedRole(req.Role, "client", "driver", "admin"))
            return BadRequest("Role invalido.");
        if (req.DriverRouteToken is { Length: > 128 })
            return BadRequest("DriverRouteToken demasiado largo.");
        if (req.OrderToken is { Length: > 128 })
            return BadRequest("OrderToken demasiado largo.");

        return null;
    }

    private BadRequestObjectResult? ValidateNotificationPayload(NotificationPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Title) || payload.Title.Length > 120)
            return BadRequest("Titulo requerido o demasiado largo.");
        if (string.IsNullOrWhiteSpace(payload.Body) || payload.Body.Length > 500)
            return BadRequest("Mensaje requerido o demasiado largo.");
        if (payload.Url is { Length: > 2048 } ||
            payload.Icon is { Length: > 2048 } ||
            payload.Tag is { Length: > 128 } ||
            payload.Type is { Length: > 64 })
            return BadRequest("Payload demasiado largo.");

        return null;
    }

    private static bool IsAllowedRole(string? role, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(role)) return true;
        return allowed.Contains(role.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeRole(string? role, string defaultRole) =>
        string.IsNullOrWhiteSpace(role) ? defaultRole : role.Trim().ToLowerInvariant();
}

// ═══════════════════════════════════════════
//  DTOs
// ═══════════════════════════════════════════

public class PushSubscriptionRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public PushKeys Keys { get; set; } = new();

    /// <summary>OBSOLETO: se ignora. La clienta se deriva de <see cref="OrderToken"/>.</summary>
    public int? ClientId { get; set; }

    public string? Role { get; set; }                // "client" | "driver" | "admin"
    public string? DriverRouteToken { get; set; }     // Token de ruta (solo para chofer)

    /// <summary>Token de acceso del pedido (solo para clienta): prueba de que el enlace es suyo.</summary>
    public string? OrderToken { get; set; }
}

public class PushKeys
{
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
}

public class NotificationPayload
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Url { get; set; }
    public string? Tag { get; set; }
    public string? Type { get; set; }
}

public class FcmSubscribeRequest
{
    public string FcmToken { get; set; } = string.Empty;
    public string? Role { get; set; }              // "driver" | "admin"
    public string? DriverRouteToken { get; set; }  // Token de ruta activa (opcional)
}
