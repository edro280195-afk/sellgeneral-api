using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerOrdersService
{
    /// <summary>
    /// Lista los pedidos de la compradora, cross-tenant, con paginación y
    /// filtro por estado. El scoping por AccountId se hace explícito con
    /// IgnoreQueryFilters (la app Flutter de la compradora no envía
    /// X-Business-Id, así que el tenant filter global no se aplica).
    /// </summary>
    Task<BuyerOrdersResponse> GetOrdersAsync(
        int accountId,
        string filter = BuyerOrderFilter.All,
        int? businessId = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Listado paginado de pedidos de la compradora. Lee cross-tenant por
/// AccountId (igual que BuyerFeedService) y arma un DTO ligero con marca,
/// conteo de items y AccessToken para abrir rastreo desde la app.
/// </summary>
public class BuyerOrdersService : IBuyerOrdersService
{
    private const string DefaultBrandColor = "#FB6F9C";
    private const int MaxPageSize = 50;

    private static readonly HashSet<OrderStatus> OpenStatuses = new()
    {
        OrderStatus.Pending,
        OrderStatus.Confirmed,
        OrderStatus.Shipped,
        OrderStatus.InRoute,
    };

    private static readonly HashSet<OrderStatus> ClosedStatuses = new()
    {
        OrderStatus.Delivered,
        OrderStatus.Canceled,
        OrderStatus.NotDelivered,
        OrderStatus.Postponed,
    };

    private readonly AppDbContext _db;

    public BuyerOrdersService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BuyerOrdersResponse> GetOrdersAsync(
        int accountId,
        string filter = BuyerOrderFilter.All,
        int? businessId = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        filter = NormalizeFilter(filter);
        page = page < 1 ? 1 : page;
        pageSize = pageSize switch
        {
            < 1 => 20,
            > MaxPageSize => MaxPageSize,
            _ => pageSize,
        };

        var clientIds = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (clientIds.Count == 0)
        {
            return new BuyerOrdersResponse(
                new List<BuyerOrderDto>(), 0, page, pageSize, filter, businessId);
        }

        var query = _db.Orders.AsNoTracking().IgnoreQueryFilters()
            .Where(o => clientIds.Contains(o.ClientId));

        if (businessId is not null)
        {
            query = query.Where(o => o.BusinessId == businessId.Value);
        }

        query = filter switch
        {
            BuyerOrderFilter.Open => query.Where(o => OpenStatuses.Contains(o.Status)),
            BuyerOrderFilter.Closed => query.Where(o => ClosedStatuses.Contains(o.Status)),
            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
            .ToListAsync(cancellationToken);

        var businessIds = rows.Select(r => r.BusinessId).Distinct().ToList();
        var businesses = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => businessIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Name, b.BrandPrimaryColor, b.LogoUrl })
            .ToListAsync(cancellationToken);
        var bizById = businesses.ToDictionary(b => b.Id);

        string brandOf(int id) =>
            bizById.TryGetValue(id, out var b) && !string.IsNullOrWhiteSpace(b.BrandPrimaryColor)
                ? b.BrandPrimaryColor
                : DefaultBrandColor;
        string nameOf(int id) => bizById.TryGetValue(id, out var b) ? b.Name : "";
        string? logoOf(int id) => bizById.TryGetValue(id, out var b) ? b.LogoUrl : null;

        var orders = rows.Select(o => new BuyerOrderDto(
            o.Id,
            o.BusinessId,
            nameOf(o.BusinessId),
            brandOf(o.BusinessId),
            logoOf(o.BusinessId),
            o.Status.ToString(),
            o.ItemsCount,
            o.Total,
            o.AccessToken,
            o.CreatedAt,
            o.ScheduledDeliveryDate)).ToList();

        return new BuyerOrdersResponse(orders, total, page, pageSize, filter, businessId);
    }

    private static string NormalizeFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return BuyerOrderFilter.All;
        if (string.Equals(filter, BuyerOrderFilter.Open, StringComparison.OrdinalIgnoreCase))
            return BuyerOrderFilter.Open;
        if (string.Equals(filter, BuyerOrderFilter.Closed, StringComparison.OrdinalIgnoreCase))
            return BuyerOrderFilter.Closed;
        return BuyerOrderFilter.All;
    }
}
