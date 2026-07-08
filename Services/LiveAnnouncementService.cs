using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface ILiveAnnouncementService
{
    /// <summary>
    /// Marca "estoy en vivo ahora". Lanza <see cref="LiveAnnouncementAlreadyActiveException"/>
    /// si ya hay uno activo (la UI debe ofrecer terminarlo primero).
    /// </summary>
    Task<LiveAnnouncementDto> StartAsync(
        StartLiveAnnouncementRequest request, CancellationToken cancellationToken = default);

    Task EndAsync(int id, CancellationToken cancellationToken = default);

    Task<LiveAnnouncementDto?> GetActiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Excepción que se traduce a 409 en el controller.</summary>
public class LiveAnnouncementAlreadyActiveException : Exception
{
    public LiveAnnouncementAlreadyActiveException(LiveAnnouncementDto active)
        : base("Ya tienes un vivo activo. Termínalo antes de iniciar otro.")
    {
        Active = active;
    }

    public LiveAnnouncementDto Active { get; }
}

/// <summary>
/// TTL de 3 horas: si la vendedora olvida cerrarlo, se considera terminado
/// solo. Tenant-scoped (ITenantOwned): el query filter automático ya acota
/// al negocio activo, no hace falta IgnoreQueryFilters ni asignar
/// BusinessId a mano (lo hace StampTenantOwnedEntities al guardar).
/// </summary>
public class LiveAnnouncementService : ILiveAnnouncementService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(3);

    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly IPushNotificationService _push;

    public LiveAnnouncementService(AppDbContext db, ICurrentTenant tenant, IPushNotificationService push)
    {
        _db = db;
        _tenant = tenant;
        _push = push;
    }

    public async Task<LiveAnnouncementDto> StartAsync(
        StartLiveAnnouncementRequest request, CancellationToken cancellationToken = default)
    {
        var active = await FindActiveAsync(cancellationToken);
        if (active is not null)
        {
            throw new LiveAnnouncementAlreadyActiveException(ToDto(active));
        }

        var title = request.Title?.Trim();
        var announcement = new LiveAnnouncement
        {
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            StartedAt = DateTime.UtcNow,
        };
        _db.LiveAnnouncements.Add(announcement);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await _push.SendNotificationToFollowersAsync(
                _tenant.ActiveBusinessId,
                title: announcement.Title is null ? "¡Estamos en vivo!" : $"¡En vivo! {announcement.Title}",
                message: "Toca para entrar antes de que se acabe.",
                url: $"/store/{_tenant.ActiveBusinessId}",
                tag: "live-started",
                requireNotifyOnLive: true);
        }
        catch
        {
            // El push no debe tumbar el aviso ya creado.
        }

        return ToDto(announcement);
    }

    public async Task EndAsync(int id, CancellationToken cancellationToken = default)
    {
        var announcement = await _db.LiveAnnouncements
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (announcement is null || announcement.EndedAt is not null) return;

        announcement.EndedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LiveAnnouncementDto?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var active = await FindActiveAsync(cancellationToken);
        return active is null ? null : ToDto(active);
    }

    private async Task<LiveAnnouncement?> FindActiveAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - Ttl;
        return await _db.LiveAnnouncements
            .Where(a => a.EndedAt == null && a.StartedAt > cutoff)
            .OrderByDescending(a => a.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static LiveAnnouncementDto ToDto(LiveAnnouncement a) =>
        new(a.Id, a.Title, a.StartedAt, a.EndedAt is null);
}
