using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerRafflesService
{
    /// <summary>
    /// Devuelve los sorteos de las tiendas donde la compradora tiene un
    /// Client reclamado (cross-tenant por AccountId). Excluye los sorteos
    /// en estado "Draft" (aún no se publicaron). Enriqu cada sorteo con
    /// su `MyEntryCount` y si resultó ganadora.
    /// </summary>
    Task<List<MyRaffleDto>> GetMyRafflesAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sorteos vistos por la compradora. Trae los sorteos de sus tiendas
/// (excluyendo Draft), y los une con `RaffleEntry` y `RaffleParticipant`
/// para detectar boletos y ganadores. Una compradora puede aparecer en
/// varios Client de un mismo negocio (raro); sumamos todos sus boletos.
/// </summary>
public class BuyerRafflesService : IBuyerRafflesService
{
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;

    public BuyerRafflesService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<MyRaffleDto>> GetMyRafflesAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var clients = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => new { c.Id, c.BusinessId })
            .ToListAsync(cancellationToken);

        if (clients.Count == 0)
        {
            return new List<MyRaffleDto>();
        }

        var clientIds = clients.Select(c => c.Id).ToList();
        var businessIds = clients.Select(c => c.BusinessId).Distinct().ToList();

        // Trae sorteos no-Draft de mis tiendas + los que ya me apuntan como
        // participante (por si quedaron en Draft pero ya tengo entrada).
        var myRaffleIdsFromParticipants = await _db.RaffleParticipants
            .AsNoTracking().IgnoreQueryFilters()
            .Where(rp => clientIds.Contains(rp.ClientId))
            .Select(rp => rp.RaffleId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var myRaffleIdsFromEntries = await _db.RaffleEntries
            .AsNoTracking().IgnoreQueryFilters()
            .Where(re => clientIds.Contains(re.ClientId))
            .Select(re => re.RaffleId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var myRaffleIds = myRaffleIdsFromParticipants
            .Concat(myRaffleIdsFromEntries)
            .Distinct()
            .ToHashSet();

        var visibleStatuses = new[] { "Active", "Completed", "Cancelled" };
        var raffles = await _db.Raffles.AsNoTracking().IgnoreQueryFilters()
            .Where(r =>
                (businessIds.Contains(r.BusinessId) && visibleStatuses.Contains(r.Status))
                || myRaffleIds.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.BusinessId,
                r.Name,
                r.ImageUrl,
                r.PrizeType,
                r.PrizeValue,
                r.PrizeDescription,
                r.RaffleDate,
                r.Status,
                r.AnnouncedAt,
                r.WinnerId,
                TandaName = r.Tanda != null ? r.Tanda.Name : null,
            })
            .ToListAsync(cancellationToken);

        if (raffles.Count == 0)
        {
            return new List<MyRaffleDto>();
        }

        var raffleIds = raffles.Select(r => r.Id).ToList();

        // Boletos: cuenta entradas por (raffle, client) agrupadas por raffle.
        var entriesByRaffle = await _db.RaffleEntries.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(re => raffleIds.Contains(re.RaffleId)
                         && clientIds.Contains(re.ClientId))
            .GroupBy(re => re.RaffleId)
            .Select(g => new { RaffleId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var entryCountByRaffle = entriesByRaffle.ToDictionary(e => e.RaffleId, e => e.Count);

        // Participantes marcados como ganadores: cualquier RaffleParticipant
        // con IsWinner = true en mis sorteos. Como una compradora puede tener
        // varios Client, marcamos ganador si AL MENOS uno aparece como ganador.
        var winnersByRaffle = await _db.RaffleParticipants.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(rp => raffleIds.Contains(rp.RaffleId)
                         && clientIds.Contains(rp.ClientId)
                         && rp.IsWinner)
            .Select(rp => rp.RaffleId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var amIWinnerSet = winnersByRaffle.ToHashSet();

        var bizIdsToLoad = businessIds
            .Concat(raffles.Select(r => r.BusinessId))
            .Distinct()
            .ToList();
        var businesses = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => bizIdsToLoad.Contains(b.Id))
            .Select(b => new BizLite(b.Id, b.Name, b.BrandPrimaryColor ?? ""))
            .ToListAsync(cancellationToken);
        var bizById = businesses.ToDictionary(b => b.Id);

        var result = new List<MyRaffleDto>();
        foreach (var r in raffles.OrderByDescending(r => r.RaffleDate))
        {
            var biz = bizById.TryGetValue(r.BusinessId, out var b) ? b : null;
            var brandColor = !string.IsNullOrWhiteSpace(biz?.BrandPrimaryColor)
                ? biz!.BrandPrimaryColor
                : DefaultBrandColor;
            var entryCount = entryCountByRaffle.TryGetValue(r.Id, out var c) ? c : 0;
            var amIWinner = amIWinnerSet.Contains(r.Id) ||
                (r.WinnerId.HasValue && clientIds.Contains(r.WinnerId.Value));
            var fallbackClient = clients.First(c => c.BusinessId == r.BusinessId);

            result.Add(new MyRaffleDto(
                RaffleId: r.Id,
                BusinessId: r.BusinessId,
                BusinessName: biz?.Name ?? "",
                BrandPrimaryColor: brandColor,
                ClientId: fallbackClient.Id,
                Name: r.Name,
                ImageUrl: r.ImageUrl,
                PrizeType: r.PrizeType,
                PrizeValue: r.PrizeValue,
                PrizeDescription: r.PrizeDescription,
                RaffleDate: r.RaffleDate,
                Status: r.Status,
                TandaName: r.TandaName,
                MyEntryCount: entryCount,
                IsMineEntered: entryCount > 0 || myRaffleIds.Contains(r.Id),
                AmIWinner: amIWinner,
                AnnouncedAt: r.AnnouncedAt));
        }

        return result;
    }

    private record BizLite(int Id, string Name, string BrandPrimaryColor);
}
