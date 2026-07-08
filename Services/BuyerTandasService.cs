using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerTandasService
{
    /// <summary>
    /// Devuelve las tandas activas/draft/completed de las tiendas donde la
    /// compradora tiene un Client reclamado (cross-tenant por AccountId).
    /// Marca `IsMine` y enriquece con su turno, pagos y si gana esta semana.
    /// </summary>
    Task<List<MyTandaDto>> GetMyTandasAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tandas vistas por la compradora en la app Flutter. Une los Client de la
/// Account con TandaParticipant, y trae también las tandas activas de las
/// mismas tiendas donde aún NO está inscrita, para que la app pueda
/// distinguir "Mis tandas" vs "Disponibles".
/// </summary>
public class BuyerTandasService : IBuyerTandasService
{
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;

    public BuyerTandasService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<MyTandaDto>> GetMyTandasAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var clients = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => new { c.Id, c.BusinessId })
            .ToListAsync(cancellationToken);

        if (clients.Count == 0)
        {
            return new List<MyTandaDto>();
        }

        var clientIds = clients.Select(c => c.Id).ToList();
        var businessIds = clients.Select(c => c.BusinessId).Distinct().ToList();

        // Mis participaciones (puede haber más de una Client por tienda).
        var myParticipations = await _db.TandaParticipants.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(tp => clientIds.Contains(tp.CustomerId))
            .Select(tp => new
            {
                tp.TandaId,
                tp.CustomerId,
                tp.AssignedTurn,
                PaidWeeks = tp.Payments
                    .Where(p => p.IsVerified)
                    .Select(p => p.WeekNumber)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        // Si la compradora tiene varios Client en la misma tienda, nos quedamos
        // con la primera participación de cada (tanda, cliente) por orden de turno.
        var myByKey = myParticipations
            .GroupBy(p => (TandaId: p.TandaId, CustomerId: p.CustomerId))
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.AssignedTurn).First());

        var myTandaIds = myByKey.Keys.Select(k => k.TandaId).ToHashSet();

        var targetStatuses = new[] { "Active", "Draft", "Completed" };
        var tandas = await _db.Tandas.AsNoTracking().IgnoreQueryFilters()
            .Where(t =>
                (businessIds.Contains(t.BusinessId) && targetStatuses.Contains(t.Status))
                || myTandaIds.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.BusinessId,
                t.Name,
                t.TotalWeeks,
                t.WeeklyAmount,
                t.StartDate,
                t.Status,
                ProductName = t.Product != null ? t.Product.Name : "",
            })
            .ToListAsync(cancellationToken);

        if (tandas.Count == 0)
        {
            return new List<MyTandaDto>();
        }

        var bizIdsToLoad = businessIds
            .Concat(tandas.Select(t => t.BusinessId))
            .Distinct()
            .ToList();
        var businesses = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => bizIdsToLoad.Contains(b.Id))
            .Select(b => new BizLite(b.Id, b.Name, b.BrandPrimaryColor ?? ""))
            .ToListAsync(cancellationToken);
        var bizById = businesses.ToDictionary(b => b.Id);

        var today = DateTime.UtcNow.Date;
        var result = new List<MyTandaDto>();

        foreach (var t in tandas.OrderByDescending(t => t.StartDate))
        {
            var currentWeek = TandaWeekCalculator.CalculateClampedCurrentWeek(
                t.StartDate,
                t.TotalWeeks,
                today);

            // Buscar la participación de la compradora en esta tanda
            // (puede estar en cualquiera de sus Client de la misma tienda).
            var myParticipation = clients
                .Where(c => c.BusinessId == t.BusinessId)
                .Select(c => myByKey.TryGetValue((TandaId: t.Id, CustomerId: c.Id), out var p)
                    ? p
                    : null)
                .FirstOrDefault(p => p != null);

            var biz = bizById.TryGetValue(t.BusinessId, out var b) ? b : null;
            var brandColor = !string.IsNullOrWhiteSpace(biz?.BrandPrimaryColor)
                ? biz!.BrandPrimaryColor
                : DefaultBrandColor;

            if (myParticipation is null)
            {
                // Disponible: cualquier Client de la tienda sirve como referencia.
                var fallbackClient = clients.First(c => c.BusinessId == t.BusinessId);
                result.Add(new MyTandaDto(
                    TandaId: t.Id,
                    BusinessId: t.BusinessId,
                    BusinessName: biz?.Name ?? "",
                    BrandPrimaryColor: brandColor,
                    ClientId: fallbackClient.Id,
                    Name: t.Name,
                    ProductName: t.ProductName,
                    TotalWeeks: t.TotalWeeks,
                    WeeklyAmount: t.WeeklyAmount,
                    StartDate: t.StartDate,
                    Status: t.Status,
                    CurrentWeek: currentWeek,
                    IsMine: false,
                    MyTurn: null,
                    HasPaidThisWeek: null,
                    PaidWeeks: new List<int>(),
                    AmIThisWeekWinner: null));
            }
            else
            {
                var paid = myParticipation.PaidWeeks.OrderBy(w => w).ToList();
                result.Add(new MyTandaDto(
                    TandaId: t.Id,
                    BusinessId: t.BusinessId,
                    BusinessName: biz?.Name ?? "",
                    BrandPrimaryColor: brandColor,
                    ClientId: myParticipation.CustomerId,
                    Name: t.Name,
                    ProductName: t.ProductName,
                    TotalWeeks: t.TotalWeeks,
                    WeeklyAmount: t.WeeklyAmount,
                    StartDate: t.StartDate,
                    Status: t.Status,
                    CurrentWeek: currentWeek,
                    IsMine: true,
                    MyTurn: myParticipation.AssignedTurn,
                    HasPaidThisWeek: paid.Contains(currentWeek),
                    PaidWeeks: paid,
                    AmIThisWeekWinner: myParticipation.AssignedTurn == currentWeek));
            }
        }

        return result;
    }

    private record BizLite(int Id, string Name, string BrandPrimaryColor);
}
