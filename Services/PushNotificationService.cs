using System.Globalization;
using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using WebPush;
using System.Text.Json;

namespace EntregasApi.Services;

/// <summary>
/// Punto único de salida de notificaciones. Cada método fija EXPLÍCITAMENTE quién
/// las recibe y nunca se mezclan:
///
///   • Clienta (una persona, por pedido)  → <see cref="SendNotificationToClientAsync"/>
///   • Clientas (seguidoras de una tienda) → <see cref="SendNotificationToFollowersAsync"/>
///   • Vendedoras (dueña/administradoras)  → <see cref="SendNotificationToBusinessOwnersAsync"/>
///   • Chofer de una ruta                  → <see cref="NotifyDriverFcmAsync"/> / <see cref="NotifyDriversNewRouteAsync"/>
///
/// Ningún método resuelve destinatarios con el "negocio activo" de la petición
/// (que cae al negocio #1 cuando no hay token): el negocio o la clienta siempre
/// se pasan y se filtran de forma explícita.
///
/// Cada aviso a una cuenta (app nativa) viaja por FCM contra sus
/// <see cref="BuyerDeviceToken"/> con un payload <c>data</c> con esta forma, que la
/// app valida antes de mostrarlo o navegar:
///   type       → etiqueta del aviso (ej. "delivered", "pedidos-por-vencer")
///   audience   → "buyer" o "seller" (ver <see cref="NotificationAudience"/>)
///   businessId → negocio del que viene
///   url        → ruta interna de la app ("" si no hay)
///   accountId  → solo cuando el aviso es para UNA cuenta concreta (clienta)
/// </summary>
public interface IPushNotificationService
{
    /// <summary>
    /// Avisa a UNA clienta (por ejemplo, su pedido salió). Persiste la
    /// <see cref="Notification"/> (destinatario "buyer"), empuja FCM a los
    /// dispositivos de la cuenta enlazada a esa clienta (si la hay) y Web Push a
    /// quien abrió el enlace público de su pedido. Jamás llega a otra clienta ni
    /// a las dueñas del negocio.
    /// </summary>
    Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null, string? tag = null);
    Task SendNotificationToDriverAsync(string routeToken, string title, string message, string? url = null, string? tag = null);

    /// <summary>
    /// Avisa a quienes tienen <see cref="Models.MembershipRole.Owner"/> o
    /// <see cref="Models.MembershipRole.Admin"/> en ESE negocio (no a choferes ni a
    /// escaneadores, y no a otros negocios): persiste una <see cref="Models.Notification"/>
    /// por destinataria (destinatario "seller") y empuja push real vía FCM contra
    /// <see cref="Models.BuyerDeviceToken"/>. Además avisa por Web Push a los
    /// paneles web de ese negocio suscritos como administración.
    /// </summary>
    Task SendNotificationToBusinessOwnersAsync(int businessId, string title, string message, string? url = null, string? tag = null);

    /// <summary>
    /// Fan-out a las seguidoras (<see cref="Models.StoreFollower"/>) de una
    /// tienda: persiste una <see cref="Models.Notification"/> por
    /// destinataria (destinatario "buyer") y empuja push real vía FCM
    /// (<see cref="IFcmService"/>) contra <see cref="Models.BuyerDeviceToken"/> — NO
    /// vía Web Push/PushSubscriptions (ese camino no llega a la app nativa).
    /// </summary>
    Task SendNotificationToFollowersAsync(
        int businessId,
        string title,
        string message,
        string? url = null,
        string? tag = null,
        bool vipOnly = false,
        bool requireNotifyOnPost = false,
        bool requireNotifyOnLive = false);

    // Helpers específicos — clienta (app nativa + enlace web)
    Task NotifyClientDriverEnRouteAsync(int clientId, string? driverName = null);
    Task NotifyClientDriverNearbyAsync(int clientId, int distanceMeters);
    Task NotifyClientDeliveredAsync(int clientId);

    // FCM — App Android nativa (repartidores)
    Task NotifyDriversNewRouteAsync(string routeName, string driverToken, int deliveryCount);
    Task NotifyDriverFcmAsync(string driverRouteToken, string title, string body, Dictionary<string, string>? data = null);
}

public class PushNotificationService : IPushNotificationService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PushNotificationService> _logger;
    private readonly IFcmService _fcm;
    private readonly IFcmService _driverFcm;

    /// <param name="driverFcm">
    /// FCM de los choferes (su propio proyecto de Firebase). Sin él se usa <paramref name="fcm"/>.
    /// </param>
    public PushNotificationService(
        AppDbContext db,
        IConfiguration config,
        ILogger<PushNotificationService> logger,
        IFcmService fcm,
        IDriverFcmService? driverFcm = null)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _fcm = fcm;
        _driverFcm = driverFcm ?? fcm;
    }

    // ═══════════════════════════════════════════
    //  ENVÍO POR DESTINATARIO
    // ═══════════════════════════════════════════

    public async Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null, string? tag = null)
    {
        var client = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == clientId)
            .Select(c => new { c.Id, c.BusinessId, c.AccountId })
            .FirstOrDefaultAsync();

        if (client is null)
        {
            _logger.LogWarning("Aviso a la clienta {ClientId} omitido: la clienta no existe.", clientId);
            return;
        }

        // 1. Historial: la clienta lo ve en la app (cross-tenant vía Client.AccountId).
        try
        {
            _db.Notifications.Add(new Models.Notification
            {
                Id = Guid.NewGuid(),
                BusinessId = client.BusinessId,
                ClientId = client.Id,
                Audience = NotificationAudience.Buyer,
                Title = title,
                Message = message,
                Tag = tag ?? "general",
                Url = url,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No pude persistir la notificación para client {ClientId}", clientId);
            // Continuar: la notificación push es lo importante.
        }

        // 2. App nativa (FCM): solo a los dispositivos de la cuenta enlazada a ESTA
        //    clienta. Sin cuenta (clienta que la vendedora creó y aún no reclamó su
        //    perfil) no hay dispositivo al que empujar.
        if (client.AccountId is int accountId)
        {
            await SendFcmToAccountsAsync(
                new[] { accountId },
                title,
                message,
                BuildFcmData(NotificationAudience.Buyer, tag, client.BusinessId, url, accountId));
        }

        // 3. Web Push (enlace público del pedido): solo las suscripciones de ESTA
        //    clienta en ESTE negocio, sin depender del "negocio activo" de la petición.
        var subscriptions = await _db.PushSubscriptions.IgnoreQueryFilters()
            .Where(s => s.BusinessId == client.BusinessId
                        && s.Role == "client"
                        && s.ClientId == client.Id)
            .ToListAsync();

        await SendToSubscriptionsAsync(subscriptions, title, message, url, tag);
    }

    public async Task SendNotificationToDriverAsync(string routeToken, string title, string message, string? url = null, string? tag = null)
    {
        var subscriptions = await _db.PushSubscriptions
            .Where(s => s.Role == "driver" && s.DriverRouteToken == routeToken)
            .ToListAsync();

        await SendToSubscriptionsAsync(subscriptions, title, message, url, tag);
    }

    public async Task SendNotificationToBusinessOwnersAsync(int businessId, string title, string message, string? url = null, string? tag = null)
    {
        var accountIds = await _db.Memberships.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.BusinessId == businessId
                        && (m.Role == MembershipRole.Owner || m.Role == MembershipRole.Admin))
            .Select(m => m.AccountId)
            .Distinct()
            .ToListAsync();

        if (accountIds.Count > 0)
        {
            // Persistir una Notification por destinataria (mismo patrón que
            // SendNotificationToFollowersAsync) para que aparezca en su historial
            // aunque en ese momento no reciba el push. Destinatario "seller": no se
            // enlaza a ningún Client aunque la dueña también sea clienta de su tienda.
            await PersistForAccountsAsync(
                NotificationAudience.Seller, businessId, accountIds, title, message, url, tag, linkClients: false);

            await SendFcmToAccountsAsync(
                accountIds,
                title,
                message,
                BuildFcmData(NotificationAudience.Seller, tag, businessId, url));
        }

        // Paneles web de ESTE negocio suscritos como administración (Web Push).
        // Solo quedan suscripciones legítimas: subscribe exige sesión de
        // dueña/administradora para el rol "admin".
        var adminSubscriptions = await _db.PushSubscriptions.IgnoreQueryFilters()
            .Where(s => s.BusinessId == businessId && s.Role == "admin")
            .ToListAsync();

        await SendToSubscriptionsAsync(adminSubscriptions, title, message, url: null, tag);
    }

    public async Task SendNotificationToFollowersAsync(
        int businessId,
        string title,
        string message,
        string? url = null,
        string? tag = null,
        bool vipOnly = false,
        bool requireNotifyOnPost = false,
        bool requireNotifyOnLive = false)
    {
        var followersQuery = _db.StoreFollowers.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.BusinessId == businessId && f.UnfollowedAt == null);

        if (vipOnly)
        {
            followersQuery = followersQuery.Where(f => f.IsVip);
        }
        if (requireNotifyOnPost)
        {
            followersQuery = followersQuery.Where(f => f.NotifyOnPost);
        }
        if (requireNotifyOnLive)
        {
            followersQuery = followersQuery.Where(f => f.NotifyOnLive);
        }

        var accountIds = await followersQuery.Select(f => f.AccountId).Distinct().ToListAsync();
        if (accountIds.Count == 0) return;

        // Persistir una Notification por destinataria (mismo patrón que
        // SendNotificationToClientAsync). Cuando la seguidora ya tiene un
        // Client en este negocio se guarda también el ClientId (por si algo
        // más lo necesita), pero el historial se resuelve por AccountId.
        await PersistForAccountsAsync(
            NotificationAudience.Buyer, businessId, accountIds, title, message, url, tag, linkClients: true);

        await SendFcmToAccountsAsync(
            accountIds,
            title,
            message,
            BuildFcmData(NotificationAudience.Buyer, tag, businessId, url));
    }

    // ═══════════════════════════════════════════
    //  HELPERS ESPECÍFICOS
    // ═══════════════════════════════════════════

    public Task NotifyClientDriverEnRouteAsync(int clientId, string? driverName = null)
    {
        return SendNotificationToClientAsync(
            clientId,
            "🚗 ¡Tu pedido va en camino!",
            $"{driverName ?? "El repartidor"} salió hacia tu domicilio. ¡Prepárate! 💕",
            tag: "driver-en-route"
        );
    }

    public Task NotifyClientDriverNearbyAsync(int clientId, int distanceMeters)
    {
        var distText = distanceMeters < 100
            ? "a menos de 100 metros"
            : $"a {distanceMeters} metros";

        return SendNotificationToClientAsync(
            clientId,
            "📍 ¡El repartidor está muy cerca!",
            $"Tu repartidor se encuentra {distText} de tu domicilio. ¡Ya casi llega! 🎉",
            tag: "driver-nearby"
        );
    }

    public Task NotifyClientDeliveredAsync(int clientId)
    {
        return SendNotificationToClientAsync(
            clientId,
            "💝 ¡Pedido entregado!",
            "¡Tu pedido ha sido entregado! Gracias por tu compra 🌸",
            tag: "delivered"
        );
    }

    // ═══════════════════════════════════════════
    //  FCM — APP ANDROID NATIVA (REPARTIDORES)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Avisa de una nueva ruta. Si ya hay dispositivos de chofer registrados con el
    /// token de ESA ruta, solo les avisa a ellos. Si ninguno lo ha registrado todavía
    /// (el chofer aún no abre la ruta, y los choferes no tienen cuenta con la que
    /// identificarlos), avisa a los choferes del negocio para que no se pierda.
    /// </summary>
    public async Task NotifyDriversNewRouteAsync(string routeName, string driverToken, int deliveryCount)
    {
        var allTokens = await _db.FcmTokens
            .Where(t => t.Role == "driver")
            .Select(t => new { t.Token, t.DriverRouteToken })
            .ToListAsync();

        _logger.LogInformation("FCM nueva ruta [{Route}]: {Total} tokens de driver en BD. RouteToken={DriverToken}",
            routeName, allTokens.Count, driverToken);

        if (allTokens.Count == 0)
        {
            _logger.LogWarning("FCM nueva ruta: no hay tokens de driver registrados. El repartidor debe abrir la app para registrarse.");
            return;
        }

        var targetedTokens = allTokens
            .Where(t => t.DriverRouteToken == driverToken)
            .Select(t => t.Token)
            .Distinct()
            .ToList();

        var recipients = targetedTokens.Count > 0
            ? targetedTokens
            : allTokens.Select(t => t.Token).Distinct().ToList();

        _logger.LogInformation("FCM nueva ruta: {Targeted} token(s) con RouteToken coincidente; se envía a {Recipients}.",
            targetedTokens.Count, recipients.Count);

        await _driverFcm.SendToTokensAsync(
            recipients,
            title: "🚗 Nueva ruta asignada",
            body: $"{routeName} — {deliveryCount} entregas listas para iniciar",
            data: new Dictionary<string, string>
            {
                { "type", "new_route" },
                { "driverToken", driverToken },
                { "deliveryCount", deliveryCount.ToString(CultureInfo.InvariantCulture) }
            }
        );
    }

    /// <summary>
    /// Notifica al chofer de una ruta específica por su token de ruta.
    /// </summary>
    public async Task NotifyDriverFcmAsync(string driverRouteToken, string title, string body, Dictionary<string, string>? data = null)
    {
        var tokens = await _db.FcmTokens
            .Where(t => t.Role == "driver" && t.DriverRouteToken == driverRouteToken)
            .Select(t => t.Token)
            .ToListAsync();

        foreach (var token in tokens)
        {
            await _driverFcm.SendToTokenAsync(token, title, body, data);
        }
    }

    // ═══════════════════════════════════════════
    //  CORE DE ENVÍO
    // ═══════════════════════════════════════════

    /// <summary>
    /// Guarda una <see cref="Models.Notification"/> por cuenta. Un fallo al guardar
    /// no impide el push (el push es lo importante).
    /// </summary>
    private async Task PersistForAccountsAsync(
        string audience,
        int businessId,
        IReadOnlyCollection<int> accountIds,
        string title,
        string message,
        string? url,
        string? tag,
        bool linkClients)
    {
        try
        {
            var clientIdByAccount = new Dictionary<int, int>();
            if (linkClients)
            {
                clientIdByAccount = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
                    .Where(c => c.BusinessId == businessId && c.AccountId != null
                                && accountIds.Contains(c.AccountId!.Value))
                    .Select(c => new { c.AccountId, c.Id })
                    .ToDictionaryAsync(c => c.AccountId!.Value, c => c.Id);
            }

            var now = DateTime.UtcNow;
            foreach (var accountId in accountIds)
            {
                _db.Notifications.Add(new Models.Notification
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    AccountId = accountId,
                    ClientId = clientIdByAccount.TryGetValue(accountId, out var clientId) ? clientId : null,
                    Audience = audience,
                    Title = title,
                    Message = message,
                    Tag = tag ?? "general",
                    Url = url,
                    CreatedAt = now,
                });
            }
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No pude persistir las notificaciones ({Audience}) para el negocio {BusinessId}", audience, businessId);
            // Continuar: el push es lo importante.
        }
    }

    /// <summary>
    /// Empuja FCM a todos los dispositivos de las cuentas indicadas. "Best effort":
    /// un fallo de Firebase o de la base no debe tumbar la operación que disparó el aviso.
    /// </summary>
    private async Task SendFcmToAccountsAsync(
        IReadOnlyCollection<int> accountIds,
        string title,
        string message,
        Dictionary<string, string> data)
    {
        try
        {
            var tokens = await _db.BuyerDeviceTokens.AsNoTracking()
                .Where(t => accountIds.Contains(t.AccountId))
                .Select(t => t.Token)
                .Distinct()
                .ToListAsync();

            if (tokens.Count == 0) return;

            await _fcm.SendToTokensAsync(tokens, title, message, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No pude enviar el push FCM a {Accounts} cuenta(s).", accountIds.Count);
        }
    }

    private static Dictionary<string, string> BuildFcmData(
        string audience, string? tag, int businessId, string? url, int? accountId = null)
    {
        var data = new Dictionary<string, string>
        {
            { "type", tag ?? "general" },
            { "audience", audience },
            { "businessId", businessId.ToString(CultureInfo.InvariantCulture) },
            { "url", url ?? "" },
        };
        if (accountId.HasValue)
        {
            data["accountId"] = accountId.Value.ToString(CultureInfo.InvariantCulture);
        }
        return data;
    }

    private async Task SendToSubscriptionsAsync(List<PushSubscriptionModel> subscriptions, string title, string message, string? url, string? tag = null)
    {
        if (!subscriptions.Any()) return;

        var subject = _config["VapidDetails:Subject"] ?? "mailto:info@regibazar.com";
        var publicKey = _config["VapidDetails:PublicKey"];
        var privateKey = _config["VapidDetails:PrivateKey"];

        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(privateKey))
        {
            _logger.LogWarning("VAPID Keys not configured in appsettings.json.");
            return;
        }

        var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
        var webPushClient = new WebPushClient();

        var jsonPayload = JsonSerializer.Serialize(new
        {
            notification = new
            {
                title,
                body = message,
                icon = "/assets/icons/icon-192x192.png",
                badge = "/assets/icons/icon-72x72.png",
                vibrate = new[] { 200, 100, 200 },
                tag = tag ?? "general",
                data = new { url = url ?? "/" }
            }
        });

        foreach (var sub in subscriptions)
        {
            try
            {
                var pushSubscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await webPushClient.SendNotificationAsync(pushSubscription, jsonPayload, vapidDetails);
                sub.LastUsedAt = DateTime.UtcNow;
            }
            catch (WebPushException exception)
            {
                var statusCode = exception.StatusCode;
                if (statusCode == System.Net.HttpStatusCode.Gone || statusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _db.PushSubscriptions.Remove(sub);
                }
                _logger.LogWarning($"Push failed for Endpoint {sub.Endpoint}: {exception.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending push notification.");
            }
        }

        await _db.SaveChangesAsync();
    }
}
