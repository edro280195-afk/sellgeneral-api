using EntregasApi.Data;
using EntregasApi.Services;
using Microsoft.AspNetCore.SignalR;

namespace EntregasApi.Hubs;

/// <summary>
/// Hub del panel de pedidos. Antes usaba "Admins" y "order_{accessToken}"
/// (lowercase) sin prefijo de tenant; ahora todo prefijado con "t{BusinessId}_"
/// y "order_" → "Order_" para unificar la convención (0.4).
/// </summary>
public class OrderHub : TenantAwareHubBase
{
    public OrderHub(AppDbContext db, ICurrentTenant currentTenant) : base(db, currentTenant) { }

    public async Task<bool> JoinAdminGroup(string? businessIdHeader = null)
    {
        var businessId = await ResolveBusinessFromJwtAsync(businessIdHeader);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Admins(businessId.Value));
        return true;
    }

    public async Task<bool> JoinOrderGroup(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        var businessId = await ResolveBusinessByOrderTokenAsync(accessToken);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Order(businessId.Value, accessToken));
        return true;
    }
}
