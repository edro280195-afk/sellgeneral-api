using EntregasApi.Data;
using EntregasApi.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Hubs;

/// <summary>
/// Hub del "vivo" en tiempo real. La vendedora transmite en Facebook sin
/// ningún cambio; desde aquí solo anuncia con un toque qué producto está
/// mostrando ahora mismo, y las compradoras conectadas lo ven aparecer al
/// instante. No hay ninguna conexión de datos con el video de Facebook — la
/// sincronización es que la vendedora hace ambas cosas (transmitir y
/// anunciar) al mismo tiempo.
/// </summary>
public class LiveHub : TenantAwareHubBase
{
    public LiveHub(AppDbContext db, ICurrentTenant currentTenant) : base(db, currentTenant)
    {
    }

    // ─── VENDEDORA (admin, por membership) ───

    /// <summary>Se une al grupo del vivo de su propia tienda para poder anunciar.</summary>
    public async Task<bool> JoinAdminLive(string? businessIdHeader = null)
    {
        var businessId = await ResolveBusinessFromJwtAsync(businessIdHeader);
        if (businessId is null) return false;

        SetBusiness(businessId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Live(businessId.Value));
        return true;
    }

    /// <summary>
    /// Anuncia un producto de su catálogo como "lo que muestro ahora mismo".
    /// Requiere un LiveAnnouncement activo (la vendedora ya tocó "Estoy en
    /// vivo"); actualiza sus campos Current* y hace broadcast al grupo.
    /// </summary>
    public async Task<bool> AnnounceProduct(int productId, string? businessIdHeader = null)
    {
        var businessId = await ResolveBusinessFromJwtAsync(businessIdHeader);
        if (businessId is null) return false;
        SetBusiness(businessId.Value);

        var product = await Db.Products.AsNoTracking()
            .Where(p => p.Id == productId && p.BusinessId == businessId.Value && p.IsActive)
            .Select(p => new { p.Id, p.Name, p.Price })
            .FirstOrDefaultAsync();
        if (product is null) return false;

        var announcement = await Db.LiveAnnouncements
            .Where(a => a.BusinessId == businessId.Value && a.EndedAt == null)
            .OrderByDescending(a => a.StartedAt)
            .FirstOrDefaultAsync();
        if (announcement is null) return false;

        var announcedAt = DateTime.UtcNow;
        announcement.CurrentProductId = product.Id;
        announcement.CurrentProductName = product.Name;
        announcement.CurrentProductPrice = product.Price;
        announcement.CurrentAnnouncedAt = announcedAt;
        await Db.SaveChangesAsync();

        await Clients.Group(SignalRGroupNames.Live(businessId.Value)).SendAsync("ProductAnnounced", new
        {
            productId = product.Id,
            name = product.Name,
            price = product.Price,
            announcedAt,
        });
        return true;
    }

    // ─── CLIENTA (por AccountId, cross-tenant — sin membership) ───

    /// <summary>
    /// Se une al grupo del vivo de una tienda donde ya tiene Client o ya la
    /// sigue (mismo criterio de acceso que BuyerStoreService.GetStoreAsync).
    /// </summary>
    public async Task<bool> JoinLive(int businessId)
    {
        var accountId = ReadAccountId(Context.User);
        if (accountId is null) return false;

        var hasAccess = await Db.Clients.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(c => c.AccountId == accountId.Value && c.BusinessId == businessId)
            || await Db.StoreFollowers.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(f => f.AccountId == accountId.Value && f.BusinessId == businessId && f.UnfollowedAt == null);
        if (!hasAccess) return false;

        SetBusiness(businessId);
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroupNames.Live(businessId));
        return true;
    }
}
