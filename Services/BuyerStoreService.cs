using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerStoreService
{
    /// <summary>
    /// Devuelve la vista pública de una tienda para la compradora.
    /// Lanza <see cref="StoreNotFoundException"/> si la tienda no existe o
    /// si la compradora no tiene un Client reclamado en esa tienda.
    /// </summary>
    Task<BuyerStoreDetailDto> GetStoreAsync(
        int accountId,
        int businessId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Excepción que indica que la tienda solicitada no existe o que la
/// compradora no tiene un Client reclamado en ella.
/// </summary>
public class StoreNotFoundException : Exception
{
    public StoreNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Vista de tienda para la app Flutter de la compradora. Single endpoint
/// que agrega header, puntos, live (si hay), productos activos y counts
/// para alimentar la pantalla `/store/{businessId}` con un solo request.
/// </summary>
public class BuyerStoreService : IBuyerStoreService
{
    private const int MaxProducts = 24;
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;

    public BuyerStoreService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BuyerStoreDetailDto> GetStoreAsync(
        int accountId,
        int businessId,
        CancellationToken cancellationToken = default)
    {
        var business = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == businessId)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Slug,
                b.City,
                b.LogoUrl,
                b.BrandPrimaryColor,
                b.BrandAccentColor,
                b.FacebookUrl,
                b.MessengerUrl,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (business is null)
        {
            throw new StoreNotFoundException("Tienda no encontrada.");
        }

        // Acceso: tener un Client reclamado en esta tienda O seguirla (una
        // seguidora puede entrar a curiosear sin haberle comprado nunca).
        var myClient = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId && c.BusinessId == businessId)
            .Select(c => new { c.Id, c.CurrentPoints })
            .FirstOrDefaultAsync(cancellationToken);

        var myFollow = await _db.StoreFollowers.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.BusinessId == businessId && f.AccountId == accountId && f.UnfollowedAt == null)
            .Select(f => new { f.IsVip })
            .FirstOrDefaultAsync(cancellationToken);

        if (myClient is null && myFollow is null)
        {
            throw new StoreNotFoundException("Esta tienda no está en tu cuenta.");
        }

        // Puntos de la compradora en esta tienda + próxima reward alcanzable.
        var nextRewardAt = await _db.LoyaltyRewards.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.BusinessId == businessId && r.IsActive)
            .OrderBy(r => r.PointsCost)
            .Select(r => (int?)r.PointsCost)
            .FirstOrDefaultAsync(cancellationToken);

        // Productos activos más recientes (sin imagen — el modelo no la tiene).
        var products = await _db.Products.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.BusinessId == businessId && p.IsActive)
            .OrderByDescending(p => p.Id)
            .Take(MaxProducts)
            .Select(p => new BuyerProductDto(
                p.Id, p.SKU, p.Name, p.Price, p.Stock))
            .ToListAsync(cancellationToken);

        // Counts de actividad (para que la app muestre el numerito en cada tab).
        var activeTandasCount = await _db.Tandas.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(t => t.BusinessId == businessId
                              && (t.Status == "Active" || t.Status == "Draft"),
                cancellationToken);

        var activeRafflesCount = await _db.Raffles.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(r => r.BusinessId == businessId && r.Status == "Active",
                cancellationToken);

        // Total de clientas que han comprado en la tienda (heurística barata
        // para el subtitle "X clientas" del header).
        var clientCount = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(c => c.BusinessId == businessId, cancellationToken);

        var followerCount = await _db.StoreFollowers.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(f => f.BusinessId == businessId && f.UnfollowedAt == null, cancellationToken);

        // Reseñas: promedio + conteo de OrderRating (ya se capturan al momento
        // de la entrega, flujo V3 — aquí solo se agregan como señal de
        // confianza para quien está viendo la tienda).
        var ratingStats = await _db.OrderRatings.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.BusinessId == businessId)
            .GroupBy(r => 1)
            .Select(g => new { Average = g.Average(r => (double)r.Stars), Count = g.Count() })
            .FirstOrDefaultAsync(cancellationToken);

        // "En vivo ahora": aviso en tiempo real (distinto del pipeline
        // post-hoc de LiveSession de arriba). TTL de 3h, igual que
        // LiveAnnouncementService.
        var liveNowCutoff = DateTime.UtcNow.AddHours(-3);
        var liveAnnouncement = await _db.LiveAnnouncements.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EndedAt == null && a.StartedAt > liveNowCutoff)
            .OrderByDescending(a => a.StartedAt)
            .Select(a => new
            {
                a.Title,
                a.CurrentProductId,
                a.CurrentProductName,
                a.CurrentProductPrice,
                a.CurrentAnnouncedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Verificada = tiene logo y color de acento configurados.
        var isVerified = !string.IsNullOrWhiteSpace(business.LogoUrl)
                         && !string.IsNullOrWhiteSpace(business.BrandAccentColor);

        var brand = !string.IsNullOrWhiteSpace(business.BrandPrimaryColor)
            ? business.BrandPrimaryColor
            : DefaultBrandColor;

        return new BuyerStoreDetailDto(
            BusinessId: business.Id,
            Name: business.Name,
            Slug: business.Slug,
            City: business.City,
            LogoUrl: business.LogoUrl,
            BrandPrimaryColor: brand,
            BrandAccentColor: business.BrandAccentColor,
            ClientCount: clientCount,
            IsVerified: isVerified,
            Points: new BuyerStorePointsDto(myClient?.CurrentPoints ?? 0, nextRewardAt),
            Products: products,
            ActiveTandasCount: activeTandasCount,
            ActiveRafflesCount: activeRafflesCount,
            FollowerCount: followerCount,
            IsFollowing: myFollow is not null,
            IsVip: myFollow?.IsVip ?? false,
            IsLiveNow: liveAnnouncement is not null,
            LiveAnnouncementTitle: liveAnnouncement?.Title,
            LiveCurrentProductId: liveAnnouncement?.CurrentProductId,
            LiveCurrentProductName: liveAnnouncement?.CurrentProductName,
            LiveCurrentProductPrice: liveAnnouncement?.CurrentProductPrice,
            LiveCurrentAnnouncedAt: liveAnnouncement?.CurrentAnnouncedAt,
            FacebookUrl: business.FacebookUrl,
            MessengerUrl: business.MessengerUrl,
            AverageRating: ratingStats?.Average,
            RatingsCount: ratingStats?.Count ?? 0);
    }
}
