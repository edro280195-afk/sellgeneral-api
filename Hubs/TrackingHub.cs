using EntregasApi.Data;
using EntregasApi.Services;
using Microsoft.AspNetCore.SignalR;

namespace EntregasApi.Hubs;

/// <summary>
/// Hub público de rastreo (cliente, link del pedido, vista de mapa en vivo).
/// Antes usaba "order_{accessToken}" (lowercase) y "Route_{driverToken}" sin
/// prefijo de tenant; ahora todo bajo "t{BusinessId}_" (0.4).
/// </summary>
public class TrackingHub : TenantAwareHubBase
{
    public TrackingHub(AppDbContext db, ICurrentTenant currentTenant) : base(db, currentTenant) { }

    public async Task<bool> JoinOrder(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        var businessId = await ResolveBusinessByOrderTokenAsync(accessToken);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Order(businessId.Value, accessToken));
        return true;
    }

    public async Task<bool> JoinAdminGroup(string? businessIdHeader = null)
    {
        var businessId = await ResolveBusinessFromJwtAsync(businessIdHeader);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Admins(businessId.Value));
        return true;
    }

    public async Task<bool> JoinRoute(string driverToken)
    {
        if (string.IsNullOrWhiteSpace(driverToken)) return false;

        var businessId = await ResolveBusinessByDriverTokenAsync(driverToken);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Route(businessId.Value, driverToken));
        return true;
    }
}
