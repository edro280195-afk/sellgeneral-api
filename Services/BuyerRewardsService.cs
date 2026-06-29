using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerRewardsService
{
    /// <summary>
    /// Devuelve el catálogo de premios activos de las tiendas donde la
    /// compradora tiene al menos un Client reclamado, junto con los puntos
    /// que tiene acumulados en cada tienda. Lectura cross-tenant por
    /// AccountId con IgnoreQueryFilters.
    /// </summary>
    Task<List<RewardsByBusinessDto>> GetRewardsAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Catálogo de recompensas canjeables para la app Flutter de la compradora.
/// Solo trae premios activos de las tiendas donde la persona ya está
/// "reclamada" (tiene un Client con AccountId). Las tiendas sin premios
/// configurados igual aparecen con rewards vacío para que la UI pueda
/// mostrar "Pronto habrá premios" en vez de un hueco.
/// </summary>
public class BuyerRewardsService : IBuyerRewardsService
{
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;

    public BuyerRewardsService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<RewardsByBusinessDto>> GetRewardsAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var clients = await _db.Clients.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => new { c.Id, c.BusinessId, c.CurrentPoints })
            .ToListAsync(cancellationToken);

        if (clients.Count == 0)
        {
            return new List<RewardsByBusinessDto>();
        }

        var businessIds = clients.Select(c => c.BusinessId).Distinct().ToList();
        var pointsByBusiness = clients
            .GroupBy(c => c.BusinessId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.CurrentPoints));

        var businesses = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => businessIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Name, b.BrandPrimaryColor, b.LogoUrl })
            .ToListAsync(cancellationToken);
        var bizById = businesses.ToDictionary(b => b.Id);

        var rewards = await _db.LoyaltyRewards.AsNoTracking().IgnoreQueryFilters()
            .Where(r => businessIds.Contains(r.BusinessId) && r.IsActive)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.PointsCost)
            .Select(r => new
            {
                r.Id,
                r.BusinessId,
                r.Name,
                r.Description,
                r.PointsCost,
                r.Type,
                r.Value,
                r.Icon,
            })
            .ToListAsync(cancellationToken);

        var rewardsByBusiness = rewards
            .GroupBy(r => r.BusinessId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new BuyerRewardDto(
                    r.Id,
                    r.Name,
                    r.Description,
                    r.PointsCost,
                    r.Type.ToString(),
                    r.Value,
                    r.Icon)).ToList());

        var result = businessIds
            .OrderByDescending(id => pointsByBusiness.TryGetValue(id, out var p) ? p : 0)
            .Select(id =>
            {
                bizById.TryGetValue(id, out var b);
                return new RewardsByBusinessDto(
                    id,
                    b?.Name ?? "",
                    !string.IsNullOrWhiteSpace(b?.BrandPrimaryColor)
                        ? b!.BrandPrimaryColor
                        : DefaultBrandColor,
                    b?.LogoUrl,
                    pointsByBusiness.TryGetValue(id, out var p) ? p : 0,
                    rewardsByBusiness.TryGetValue(id, out var rs)
                        ? rs
                        : new List<BuyerRewardDto>());
            })
            .ToList();

        return result;
    }
}
