using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerFeedService
{
    Task<BuyerHomeDto> GetHomeAsync(int accountId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Agrega el feed de inicio de la COMPRADORA: lee cross-tenant (por AccountId)
/// los Client que la persona ya reclamó y arma sus tiendas, puntos, pedido
/// activo y pedidos recientes. NO depende del negocio activo (X-Business-Id):
/// usa IgnoreQueryFilters + scoping explícito por AccountId, igual que el
/// patrón de "reclamar perfil" (0.3).
/// </summary>
public class BuyerFeedService : IBuyerFeedService
{
    private const string DefaultBrandColor = "#FB6F9C";

    private static readonly HashSet<OrderStatus> OpenStatuses = new()
    {
        OrderStatus.Pending,
        OrderStatus.Confirmed,
        OrderStatus.Shipped,
        OrderStatus.InRoute,
    };

    private readonly AppDbContext _db;

    public BuyerFeedService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BuyerHomeDto> GetHomeAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var displayName = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => a.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? "";

        var clients = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => new { c.Id, c.BusinessId, c.CurrentPoints })
            .ToListAsync(cancellationToken);

        if (clients.Count == 0)
        {
            return new BuyerHomeDto(displayName, 0, null,
                new List<BuyerStoreDto>(), new List<BuyerRecentOrderDto>(), 0);
        }

        var clientIds = clients.Select(c => c.Id).ToList();
        var businessIds = clients.Select(c => c.BusinessId).Distinct().ToList();

        var businesses = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => businessIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Name, b.Slug, b.BrandPrimaryColor, b.LogoUrl })
            .ToListAsync(cancellationToken);
        var bizById = businesses.ToDictionary(b => b.Id);

        var liveBusinessIds = await _db.LiveSessions.AsNoTracking().IgnoreQueryFilters()
            .Where(l => businessIds.Contains(l.BusinessId)
                        && l.Status == LiveSessionStatus.Ready)
            .Select(l => l.BusinessId)
            .ToListAsync(cancellationToken);
        var liveBusinesses = liveBusinessIds.ToHashSet();

        string brandOf(int id) =>
            bizById.TryGetValue(id, out var b) && !string.IsNullOrWhiteSpace(b.BrandPrimaryColor)
                ? b.BrandPrimaryColor
                : DefaultBrandColor;
        string nameOf(int id) => bizById.TryGetValue(id, out var b) ? b.Name : "";

        var totalPoints = clients.Sum(c => c.CurrentPoints);

        var stores = clients
            .GroupBy(c => c.BusinessId)
            .Select(g =>
            {
                bizById.TryGetValue(g.Key, out var b);
                return new BuyerStoreDto(
                    g.Key,
                    b?.Name ?? "",
                    b?.Slug,
                    brandOf(g.Key),
                    b?.LogoUrl,
                    g.Sum(c => c.CurrentPoints),
                    liveBusinesses.Contains(g.Key));
            })
            .OrderByDescending(s => s.Points)
            .ToList();

        var orders = await _db.Orders.AsNoTracking().IgnoreQueryFilters()
            .Where(o => clientIds.Contains(o.ClientId))
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new
            {
                o.Id,
                o.BusinessId,
                o.Status,
                o.AccessToken,
                o.CreatedAt,
                o.ScheduledDeliveryDate,
                o.Total,
                ItemsCount = o.Items.Count(),
            })
            .Take(30)
            .ToListAsync(cancellationToken);

        var active = orders.FirstOrDefault(o => OpenStatuses.Contains(o.Status));
        BuyerActiveOrderDto? activeDto = active is null
            ? null
            : new BuyerActiveOrderDto(
                active.Id,
                active.BusinessId,
                nameOf(active.BusinessId),
                brandOf(active.BusinessId),
                active.Status.ToString(),
                active.AccessToken,
                active.ScheduledDeliveryDate,
                active.Total);

        var recent = orders
            .Take(5)
            .Select(o => new BuyerRecentOrderDto(
                o.Id,
                o.BusinessId,
                nameOf(o.BusinessId),
                brandOf(o.BusinessId),
                o.Status.ToString(),
                o.ItemsCount,
                o.Total,
                o.CreatedAt))
            .ToList();

        return new BuyerHomeDto(
            displayName,
            totalPoints,
            activeDto,
            stores,
            recent,
            liveBusinessIds.Count);
    }
}
