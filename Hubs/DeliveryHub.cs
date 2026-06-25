using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Hubs;

/// <summary>
/// Hub del panel admin + chofer + clienta. Antes mezclaba "Admins" y
/// "Route_{driverToken}" sin prefijo de tenant (0.4). Ahora cada grupo se
/// prefija con "t{BusinessId}_" y la resolución del BusinessId se hace
/// desde el JWT o desde el Order.AccessToken / DriverToken de la URL.
/// </summary>
public class DeliveryHub : TenantAwareHubBase
{
    private readonly IEntitlementService _entitlements;

    public DeliveryHub(
        AppDbContext db,
        ICurrentTenant currentTenant,
        IEntitlementService entitlements) : base(db, currentTenant)
    {
        _entitlements = entitlements;
    }

    // ─── ADMIN ───
    public async Task<bool> JoinAdminGroup(string? businessIdHeader = null)
    {
        var businessId = await ResolveBusinessFromJwtAsync(businessIdHeader);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Admins(businessId.Value));
        return true;
    }

    // ─── DRIVER ───
    public async Task<bool> JoinRoute(string driverToken)
    {
        if (string.IsNullOrWhiteSpace(driverToken)) return false;

        var businessId = await ResolveBusinessByDriverTokenAsync(driverToken);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Route(businessId.Value, driverToken));
        return true;
    }

    // Reports location to Admins and Clients watching the same route
    public async Task ReportLocation(string driverToken, double lat, double lng)
    {
        var businessId = await ResolveBusinessByDriverTokenAsync(driverToken);
        if (businessId is null) return;
        SetBusiness(businessId.Value);

        // Broadcast to admins (de este tenant)
        await Clients.Group(SignalRGroupNames.Admins(businessId.Value))
            .SendAsync("ReceiveLocation", driverToken, lat, lng);

        // Broadcast to clients watching this route (de este tenant)
        if (await _entitlements.HasFeatureAsync(Feature.LiveGpsTracking, Context.ConnectionAborted))
        {
            await Clients.Group(SignalRGroupNames.Tracking(businessId.Value, driverToken))
                .SendAsync("LocationUpdate", new { latitude = lat, longitude = lng });
        }
    }

    // ─── CLIENT ───
    public async Task<bool> JoinOrder(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        var businessId = await ResolveBusinessByOrderTokenAsync(accessToken);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Order(businessId.Value, accessToken));

        // Si el pedido está en una ruta activa, también suscribe a Tracking_
        var driverToken = await Db.Orders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.AccessToken == accessToken && o.Delivery != null && o.Delivery.DeliveryRoute != null)
            .Select(o => o.Delivery!.DeliveryRoute!.DriverToken)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrEmpty(driverToken))
        {
            if (await _entitlements.HasFeatureAsync(Feature.LiveGpsTracking, Context.ConnectionAborted))
            {
                await Groups.AddToGroupAsync(
                    Context.ConnectionId,
                    SignalRGroupNames.Tracking(businessId.Value, driverToken));
            }
        }

        return true;
    }

    // ─── CHAT ───
    public async Task SendMessage(string groupName, object message)
    {
        // El nombre del grupo YA viene prefijado por el caller (controller), que
        // conoce el BusinessId. Si llega un nombre sin prefijo, lo rechazamos
        // para no permitir que un cliente mande eventos a otros tenants.
        if (string.IsNullOrWhiteSpace(groupName) || !groupName.StartsWith("t", StringComparison.Ordinal))
        {
            return;
        }
        await Clients.Group(groupName).SendAsync("ReceiveMessage", message);
    }
}
