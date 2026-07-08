using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerFollowService
{
    /// <summary>Estado actual de "seguir" de la compradora sobre una tienda.</summary>
    Task<FollowStateDto> GetStateAsync(
        int accountId, int businessId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea o reactiva el seguimiento. Lanza <see cref="FollowNotFoundException"/>
    /// si la tienda no existe.
    /// </summary>
    Task<FollowStateDto> FollowAsync(
        int accountId, int businessId, FollowPreferencesRequest? preferences,
        CancellationToken cancellationToken = default);

    /// <summary>Soft-unfollow. Idempotente: no falla si ya no seguía.</summary>
    Task UnfollowAsync(
        int accountId, int businessId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Actualiza las preferencias de notificación. Lanza
    /// <see cref="FollowNotFoundException"/> si no está siguiendo la tienda.
    /// </summary>
    Task<FollowStateDto> UpdatePreferencesAsync(
        int accountId, int businessId, FollowPreferencesRequest preferences,
        CancellationToken cancellationToken = default);
}

/// <summary>Excepción que se traduce a 404 en el controller.</summary>
public class FollowNotFoundException : Exception
{
    public FollowNotFoundException(string message) : base(message) { }
}

/// <summary>
/// "Seguir tienda": relación cross-tenant compradora-negocio, independiente
/// de tener un Client (haber comprado). Sigue el patrón de
/// <see cref="BuyerStoreService"/>/<see cref="BuyerReserveService"/>:
/// IgnoreQueryFilters + scoping explícito por AccountId, BusinessId
/// asignado a mano al crear (para no depender del tenant activo, que no
/// existe en una request de compradora).
/// </summary>
public class BuyerFollowService : IBuyerFollowService
{
    private readonly AppDbContext _db;

    public BuyerFollowService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<FollowStateDto> GetStateAsync(
        int accountId, int businessId, CancellationToken cancellationToken = default)
    {
        var follow = await _db.StoreFollowers.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.BusinessId == businessId && f.AccountId == accountId && f.UnfollowedAt == null)
            .FirstOrDefaultAsync(cancellationToken);

        return ToDto(businessId, follow);
    }

    public async Task<FollowStateDto> FollowAsync(
        int accountId, int businessId, FollowPreferencesRequest? preferences,
        CancellationToken cancellationToken = default)
    {
        var businessExists = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(b => b.Id == businessId, cancellationToken);
        if (!businessExists)
        {
            throw new FollowNotFoundException("Tienda no encontrada.");
        }

        var existing = await _db.StoreFollowers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.BusinessId == businessId && f.AccountId == accountId,
                cancellationToken);

        if (existing is null)
        {
            existing = new StoreFollower
            {
                BusinessId = businessId,
                AccountId = accountId,
                NotifyOnPost = preferences?.NotifyOnPost ?? true,
                NotifyOnLive = preferences?.NotifyOnLive ?? true,
                CreatedAt = DateTime.UtcNow,
            };
            _db.StoreFollowers.Add(existing);
        }
        else
        {
            existing.UnfollowedAt = null;
            if (preferences is not null)
            {
                existing.NotifyOnPost = preferences.NotifyOnPost;
                existing.NotifyOnLive = preferences.NotifyOnLive;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(businessId, existing);
    }

    public async Task UnfollowAsync(
        int accountId, int businessId, CancellationToken cancellationToken = default)
    {
        var existing = await _db.StoreFollowers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                f => f.BusinessId == businessId && f.AccountId == accountId && f.UnfollowedAt == null,
                cancellationToken);

        if (existing is null) return; // idempotente

        existing.UnfollowedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<FollowStateDto> UpdatePreferencesAsync(
        int accountId, int businessId, FollowPreferencesRequest preferences,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.StoreFollowers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                f => f.BusinessId == businessId && f.AccountId == accountId && f.UnfollowedAt == null,
                cancellationToken);

        if (existing is null)
        {
            throw new FollowNotFoundException("No sigues esta tienda.");
        }

        existing.NotifyOnPost = preferences.NotifyOnPost;
        existing.NotifyOnLive = preferences.NotifyOnLive;
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(businessId, existing);
    }

    private static FollowStateDto ToDto(int businessId, StoreFollower? follow)
    {
        if (follow is null)
        {
            return new FollowStateDto(businessId, IsFollowing: false, NotifyOnPost: true, NotifyOnLive: true, IsVip: false);
        }
        return new FollowStateDto(
            businessId,
            IsFollowing: follow.UnfollowedAt == null,
            NotifyOnPost: follow.NotifyOnPost,
            NotifyOnLive: follow.NotifyOnLive,
            IsVip: follow.IsVip);
    }
}
