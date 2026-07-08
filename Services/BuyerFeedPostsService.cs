using EntregasApi.Data;
using EntregasApi.DTOs;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerFeedPostsService
{
    /// <summary>
    /// Novedades de una tienda para la compradora (tab "Novedades" de
    /// `/store/{businessId}`). Lanza <see cref="StoreNotFoundException"/> si
    /// la tienda no existe o si la compradora no la sigue ni tiene Client
    /// ahí (mismo criterio de acceso que <see cref="IBuyerStoreService"/>).
    /// Las novedades VIP-only vienen con el cuerpo vacío y <c>IsLocked=true</c>
    /// para quien no es VIP — no se filtran del todo, para invitar al VIP.
    /// </summary>
    Task<List<StorePostFeedItemDto>> GetStorePostsAsync(
        int accountId, int businessId, int page, int pageSize,
        CancellationToken cancellationToken = default);
}

public class BuyerFeedPostsService : IBuyerFeedPostsService
{
    private const int MaxPageSize = 50;
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;

    public BuyerFeedPostsService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<StorePostFeedItemDto>> GetStorePostsAsync(
        int accountId, int businessId, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var business = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == businessId)
            .Select(b => new { b.Id, b.Name, b.BrandPrimaryColor, b.LogoUrl })
            .FirstOrDefaultAsync(cancellationToken);
        if (business is null)
        {
            throw new StoreNotFoundException("Tienda no encontrada.");
        }

        var hasClient = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(c => c.AccountId == accountId && c.BusinessId == businessId, cancellationToken);
        var myFollow = await _db.StoreFollowers.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.BusinessId == businessId && f.AccountId == accountId && f.UnfollowedAt == null)
            .Select(f => new { f.IsVip })
            .FirstOrDefaultAsync(cancellationToken);

        if (!hasClient && myFollow is null)
        {
            throw new StoreNotFoundException("Esta tienda no está en tu cuenta.");
        }

        var isVip = myFollow?.IsVip ?? false;
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? 20 : pageSize;

        var brand = !string.IsNullOrWhiteSpace(business.BrandPrimaryColor)
            ? business.BrandPrimaryColor
            : DefaultBrandColor;

        var posts = await _db.StorePosts.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.BusinessId == businessId && p.DeletedAt == null)
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new { p.Id, p.Body, p.ImageUrl, p.IsVipOnly, p.CreatedAt })
            .ToListAsync(cancellationToken);

        return posts.Select(p =>
        {
            var isLocked = p.IsVipOnly && !isVip;
            return new StorePostFeedItemDto(
                Id: p.Id,
                BusinessId: business.Id,
                BusinessName: business.Name,
                BrandPrimaryColor: brand,
                LogoUrl: business.LogoUrl,
                Body: isLocked ? "" : p.Body,
                ImageUrl: isLocked ? null : p.ImageUrl,
                IsVipOnly: p.IsVipOnly,
                IsLocked: isLocked,
                CreatedAt: p.CreatedAt);
        }).ToList();
    }
}
