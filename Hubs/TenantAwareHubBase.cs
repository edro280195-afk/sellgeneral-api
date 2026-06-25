using System.Security.Claims;
using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Hubs;

/// <summary>
/// Base de los 5 hubs de SignalR (plan 0.4). Resuelve el BusinessId de la
/// conexión actual a partir del JWT o de los tokens de recurso (Order.AccessToken
/// / DeliveryRoute.DriverToken) y lo guarda en <c>Context.Items</c> para que
/// los métodos JoinX lo usen al prefijar el grupo.
///
/// Si no se puede resolver el BusinessId, el Join devuelve <c>false</c> y la
/// conexión NO se une a ningún grupo: la única forma de recibir eventos es
/// haber pasado la prueba de tenant.
/// </summary>
public abstract class TenantAwareHubBase : Hub
{
    private const string BusinessIdKey = "BusinessId";

    protected AppDbContext Db { get; }
    protected ICurrentTenant CurrentTenant { get; }

    protected TenantAwareHubBase(AppDbContext db, ICurrentTenant currentTenant)
    {
        Db = db;
        CurrentTenant = currentTenant;
    }

    /// <summary>
    /// Lee el BusinessId resuelto previamente. Lanza si la conexión no pasó
    /// por la verificación (los JoinX son los que llaman a SetBusiness primero).
    /// </summary>
    protected int GetBusinessId()
    {
        if (Context.Items.TryGetValue(BusinessIdKey, out var raw) && raw is int id)
        {
            return id;
        }
        throw new InvalidOperationException(
            "BusinessId no resuelto para esta conexión. El Join debe llamar primero a SetBusiness*.");
    }

    /// <summary>
    /// Busca el BusinessId del Order dueña del AccessToken (cross-tenant via IgnoreQueryFilters).
    /// Devuelve null si el token no existe.
    /// </summary>
    protected async Task<int?> ResolveBusinessByOrderTokenAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        return await Db.Orders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.AccessToken == accessToken)
            .Select(o => (int?)o.BusinessId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Busca el BusinessId de la DeliveryRoute dueña del DriverToken (cross-tenant).
    /// Devuelve null si el token no existe.
    /// </summary>
    protected async Task<int?> ResolveBusinessByDriverTokenAsync(string driverToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driverToken)) return null;
        return await Db.DeliveryRoutes
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => r.DriverToken == driverToken)
            .Select(r => (int?)r.BusinessId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Busca el BusinessId del Order por int orderId (cross-tenant).
    /// </summary>
    protected async Task<int?> ResolveBusinessByOrderIdAsync(int orderId, CancellationToken ct = default)
    {
        if (orderId <= 0) return null;
        return await Db.Orders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.Id == orderId)
            .Select(o => (int?)o.BusinessId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Resuelve el BusinessId desde el JWT (membership). Usa el header
    /// <c>X-Business-Id</c> que llega con la conexión; si no viene y solo hay
    /// una membership, la usa; si no, devuelve null (la UI debe pedirlo).
    /// </summary>
    protected async Task<int?> ResolveBusinessFromJwtAsync(string? requestedBusinessIdHeader, CancellationToken ct = default)
    {
        var accountId = ReadAccountId(Context.User);
        if (accountId is null) return null;

        var memberships = await Db.Memberships
            .AsNoTracking()
            .Where(m => m.AccountId == accountId.Value && m.Business!.IsActive)
            .Select(m => m.BusinessId)
            .ToListAsync(ct);

        if (memberships.Count == 0) return null;

        if (int.TryParse(requestedBusinessIdHeader, out var requested) && memberships.Contains(requested))
        {
            return requested;
        }

        return memberships.Count == 1 ? memberships[0] : null;
    }

    /// <summary>Guarda el BusinessId en Context.Items.</summary>
    protected void SetBusiness(int businessId)
    {
        Context.Items[BusinessIdKey] = businessId;
        CurrentTenant.SetBusiness(businessId);
    }

    private static int? ReadAccountId(ClaimsPrincipal? user)
    {
        if (user is null) return null;
        var raw = user.FindFirstValue("account_id")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");
        return int.TryParse(raw, out var id) ? id : (int?)null;
    }
}
