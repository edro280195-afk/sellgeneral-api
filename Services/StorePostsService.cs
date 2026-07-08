using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IStorePostsService
{
    /// <summary>
    /// Crea la novedad y dispara el fan-out a seguidoras. Lanza
    /// <see cref="StorePostVipNotAllowedException"/> si pide VIP-only sin
    /// el plan que lo permite.
    /// </summary>
    Task<StorePostDto> CreateAsync(CreateStorePostRequest request, CancellationToken cancellationToken = default);

    Task<List<StorePostDto>> GetMineAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}

/// <summary>Excepción que se traduce a 402 en el controller (igual que [RequiresFeature]).</summary>
public class StorePostVipNotAllowedException : Exception
{
    public StorePostVipNotAllowedException()
        : base("Marcar una novedad como VIP requiere el plan Pro o superior.") { }
}

public class StorePostNotFoundException : Exception
{
    public StorePostNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Novedades de la tienda (lado vendedora). Tenant-scoped: StorePost es
/// ITenantOwned, el query filter automático ya acota al negocio activo.
/// </summary>
public class StorePostsService : IStorePostsService
{
    private const int MaxPageSize = 50;

    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly IEntitlementService _entitlements;
    private readonly IPushNotificationService _push;

    public StorePostsService(
        AppDbContext db, ICurrentTenant tenant, IEntitlementService entitlements, IPushNotificationService push)
    {
        _db = db;
        _tenant = tenant;
        _entitlements = entitlements;
        _push = push;
    }

    public async Task<StorePostDto> CreateAsync(
        CreateStorePostRequest request, CancellationToken cancellationToken = default)
    {
        if (request.IsVipOnly && !await _entitlements.HasFeatureAsync(Feature.VipDrops, cancellationToken))
        {
            throw new StorePostVipNotAllowedException();
        }

        var post = new StorePost
        {
            Body = request.Body.Trim(),
            ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl,
            IsVipOnly = request.IsVipOnly,
            CreatedAt = DateTime.UtcNow,
        };
        _db.StorePosts.Add(post);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var preview = post.Body.Length > 80 ? post.Body[..80] + "..." : post.Body;
            await _push.SendNotificationToFollowersAsync(
                _tenant.ActiveBusinessId,
                title: request.IsVipOnly ? "✨ Novedad VIP" : "📣 Nueva novedad",
                message: preview,
                url: $"/store/{_tenant.ActiveBusinessId}",
                tag: "store-post",
                vipOnly: request.IsVipOnly,
                requireNotifyOnPost: true);
        }
        catch
        {
            // El push no debe tumbar la publicación ya creada.
        }

        return ToDto(post);
    }

    public async Task<List<StorePostDto>> GetMineAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? 20 : pageSize;

        return await _db.StorePosts.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new StorePostDto(p.Id, p.Body, p.ImageUrl, p.IsVipOnly, p.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var post = await _db.StorePosts.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (post is null || post.DeletedAt is not null)
        {
            throw new StorePostNotFoundException("Esta novedad no existe.");
        }

        post.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static StorePostDto ToDto(StorePost p) =>
        new(p.Id, p.Body, p.ImageUrl, p.IsVipOnly, p.CreatedAt);
}
