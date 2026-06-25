using EntregasApi.Data;
using EntregasApi.Services;
using Microsoft.AspNetCore.SignalR;

namespace EntregasApi.Hubs;

/// <summary>
/// Hub de logística. Antes usaba "Route_{routeId}" (int) y "Admins", ahora
/// unificado a "Route_{driverToken}" con prefijo de tenant (0.4).
/// </summary>
public class LogisticsHub : TenantAwareHubBase
{
    public LogisticsHub(AppDbContext db, ICurrentTenant currentTenant) : base(db, currentTenant) { }

    public async Task<bool> JoinRouteGroup(string driverToken)
    {
        if (string.IsNullOrWhiteSpace(driverToken)) return false;

        var businessId = await ResolveBusinessByDriverTokenAsync(driverToken);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Route(businessId.Value, driverToken));
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

    public async Task LeaveRouteGroup(string driverToken)
    {
        if (string.IsNullOrWhiteSpace(driverToken)) return;
        var businessId = await ResolveBusinessByDriverTokenAsync(driverToken);
        if (businessId is null) return;

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            SignalRGroupNames.Route(businessId.Value, driverToken));
    }
}
