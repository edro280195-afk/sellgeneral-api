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
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (business is null)
        {
            throw new StoreNotFoundException("Tienda no encontrada.");
        }

        // Verificar que la compradora tiene un Client reclamado en esta tienda.
        var myClient = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId && c.BusinessId == businessId)
            .Select(c => new { c.Id, c.CurrentPoints })
            .FirstOrDefaultAsync(cancellationToken);

        if (myClient is null)
        {
            throw new StoreNotFoundException("Esta tienda no está en tu cuenta.");
        }

        // Puntos de la compradora en esta tienda + próxima reward alcanzable.
        var nextRewardAt = await _db.LoyaltyRewards.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.BusinessId == businessId && r.IsActive)
            .OrderBy(r => r.PointsCost)
            .Select(r => (int?)r.PointsCost)
            .FirstOrDefaultAsync(cancellationToken);

        // Live activo: cualquier LiveSession con Status = Ready (publicado).
        // Por ahora no hay modelo de "viewerCount" en LiveSession, así que
        // devolvemos 0 como stub. Cuando se agregue el hub de viewers, se
        // conecta y se actualiza vía SignalR.
        var live = await _db.LiveSessions.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessId == businessId
                        && l.Status == LiveSessionStatus.Ready)
            .OrderByDescending(l => l.ProcessedAt ?? l.ImportedAt)
            .Select(l => new
            {
                l.Id,
                l.Title,
                l.ProcessedAt,
                TopicKeywords = l.Products
                    .OrderBy(p => p.AnnouncedAtSeconds)
                    .Take(3)
                    .Select(p => p.Keyword)
                    .ToList(),
            })
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

        // Verificada = tiene logo y color de acento configurados.
        var isVerified = !string.IsNullOrWhiteSpace(business.LogoUrl)
                         && !string.IsNullOrWhiteSpace(business.BrandAccentColor);

        var brand = !string.IsNullOrWhiteSpace(business.BrandPrimaryColor)
            ? business.BrandPrimaryColor
            : DefaultBrandColor;

        BuyerLiveSummaryDto? liveDto = null;
        if (live is not null)
        {
            string? topics = null;
            if (live.TopicKeywords.Count > 0)
            {
                topics = string.Join(", ", live.TopicKeywords);
            }
            liveDto = new BuyerLiveSummaryDto(
                SessionId: live.Id,
                Title: live.Title ?? "Live",
                ViewerCount: 0,
                Topics: topics,
                ProcessedAt: live.ProcessedAt);
        }

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
            Points: new BuyerStorePointsDto(myClient.CurrentPoints, nextRewardAt),
            Live: liveDto,
            Products: products,
            ActiveTandasCount: activeTandasCount,
            ActiveRafflesCount: activeRafflesCount);
    }
}
