using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerPaymentService
{
    /// <summary>
    /// Lista todos los pagos de la compradora, cross-tenant por
    /// AccountId, ordenados por fecha descendente. Cada pago incluye
    /// info mínima del Order y la tienda para que la app pueda agrupar
    /// y mostrar sin hacer N+1 requests.
    /// </summary>
    Task<List<BuyerPaymentDto>> GetMyPaymentsAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pagos vistos por la compradora en la app Flutter. Trae todos los
/// `OrderPayment` de los Order de los Client de la Account, con el
/// contexto mínimo del Order y la tienda.
/// </summary>
public class BuyerPaymentService : IBuyerPaymentService
{
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;

    public BuyerPaymentService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<BuyerPaymentDto>> GetMyPaymentsAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var clients = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => new { c.Id, c.BusinessId })
            .ToListAsync(cancellationToken);

        if (clients.Count == 0)
        {
            return new List<BuyerPaymentDto>();
        }

        var clientIds = clients.Select(c => c.Id).ToList();
        var businessIds = clients.Select(c => c.BusinessId).Distinct().ToList();

        var payments = await _db.OrderPayments.AsNoTracking().IgnoreQueryFilters()
            .Where(p => clientIds.Contains(p.Order.ClientId))
            .OrderByDescending(p => p.Date)
            .Select(p => new
            {
                p.Id,
                p.OrderId,
                p.BusinessId,
                p.Amount,
                p.Method,
                p.Date,
                p.RegisteredBy,
                p.Notes,
                OrderStatus = p.Order.Status.ToString(),
                OrderTotal = p.Order.Total,
                ClientId = p.Order.ClientId,
            })
            .ToListAsync(cancellationToken);

        if (payments.Count == 0)
        {
            return new List<BuyerPaymentDto>();
        }

        var businesses = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => businessIds.Contains(b.Id))
            .Select(b => new BizLite(b.Id, b.Name, b.BrandPrimaryColor, b.LogoUrl))
            .ToListAsync(cancellationToken);
        var bizById = businesses.ToDictionary(b => b.Id);

        return payments.Select(p =>
        {
            var biz = bizById.TryGetValue(p.BusinessId, out var b) ? b : null;
            return new BuyerPaymentDto(
                PaymentId: p.Id,
                OrderId: p.OrderId,
                BusinessId: p.BusinessId,
                BusinessName: biz?.Name ?? "",
                BrandPrimaryColor: !string.IsNullOrWhiteSpace(biz?.BrandPrimaryColor)
                    ? biz!.BrandPrimaryColor
                    : DefaultBrandColor,
                LogoUrl: biz?.LogoUrl,
                Amount: p.Amount,
                Method: p.Method,
                Date: p.Date,
                RegisteredBy: p.RegisteredBy,
                Notes: p.Notes,
                OrderStatus: p.OrderStatus,
                OrderTotal: p.OrderTotal);
        }).ToList();
    }

    private record BizLite(int Id, string Name, string BrandPrimaryColor, string? LogoUrl);
}
