using EntregasApi.Data;
using EntregasApi.Services;
using Microsoft.AspNetCore.SignalR;

namespace EntregasApi.Hubs;

/// <summary>
/// Hub del POS. Antes usaba "order_{orderId}" sin prefijo de tenant: dos tenants
/// con el mismo orderId colisionaban. Ahora todo bajo "t{BusinessId}_PosOrder_{id}"
/// y "t{BusinessId}_PosNodriza" (0.4).
/// </summary>
public class PosHub : TenantAwareHubBase
{
    public PosHub(AppDbContext db, ICurrentTenant currentTenant) : base(db, currentTenant) { }

    public async Task<bool> JoinOrderGroup(int orderId)
    {
        if (orderId <= 0) return false;

        var businessId = await ResolveBusinessByOrderIdAsync(orderId);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SignalRGroupNames.PosOrder(businessId.Value, orderId));
        return true;
    }

    public async Task LeaveOrderGroup(int orderId)
    {
        if (orderId <= 0) return;
        var businessId = await ResolveBusinessByOrderIdAsync(orderId);
        if (businessId is null) return;

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            SignalRGroupNames.PosOrder(businessId.Value, orderId));
    }

    public async Task<bool> JoinNodrizaGroup(string? businessIdHeader = null)
    {
        var businessId = await ResolveBusinessFromJwtAsync(businessIdHeader);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SignalRGroupNames.PosNodriza(businessId.Value));
        return true;
    }
}
