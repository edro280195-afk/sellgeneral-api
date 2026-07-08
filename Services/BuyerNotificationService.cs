using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerNotificationService
{
    /// <summary>
    /// Lista las notificaciones de la compradora, cross-tenant por
    /// AccountId, ordenadas por fecha descendente. Solo trae las
    /// notificaciones de los Client de la Account.
    /// </summary>
    Task<List<BuyerNotificationDto>> GetMyNotificationsAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca una notificación como leída. Lanza
    /// <see cref="NotificationNotFoundException"/> si no pertenece a la
    /// Account.
    /// </summary>
    Task MarkAsReadAsync(
        int accountId,
        Guid notificationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca todas las notificaciones de la compradora como leídas.
    /// Devuelve la cantidad que se actualizó.
    /// </summary>
    Task<int> MarkAllAsReadAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cuenta las notificaciones no leídas. Se usa para el badge en el
    /// Home (icono 🔔 con punto rojo).
    /// </summary>
    Task<int> CountUnreadAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

public class NotificationNotFoundException : Exception
{
    public NotificationNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Notificaciones vistas por la compradora en la app. Persistidas cada
/// vez que <see cref="IPushNotificationService.SendNotificationToClientAsync"/>
/// emite un push. La pantalla Home las consulta para el badge de no
/// leídas y la pantalla de Notificaciones las lista.
/// </summary>
public class BuyerNotificationService : IBuyerNotificationService
{
    private const string DefaultBrandColor = "#FB6F9C";

    private readonly AppDbContext _db;

    public BuyerNotificationService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<BuyerNotificationDto>> GetMyNotificationsAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Notifications.AsNoTracking().IgnoreQueryFilters()
            .Where(n => n.AccountId == accountId
                        || _db.Clients.IgnoreQueryFilters()
                            .Any(c => c.AccountId == accountId && c.Id == n.ClientId))
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new
            {
                n.Id,
                n.BusinessId,
                n.ClientId,
                n.Title,
                n.Message,
                n.Tag,
                n.Url,
                n.OrderId,
                n.CreatedAt,
                n.ReadAt,
            })
            .Take(200)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return new List<BuyerNotificationDto>();
        }

        var businessIds = rows.Select(r => r.BusinessId).Distinct().ToList();
        var businesses = await _db.Businesses.AsNoTracking().IgnoreQueryFilters()
            .Where(b => businessIds.Contains(b.Id))
            .Select(b => new BizLite(b.Id, b.Name, b.BrandPrimaryColor))
            .ToListAsync(cancellationToken);
        var bizById = businesses.ToDictionary(b => b.Id);

        return rows.Select(n =>
        {
            var biz = bizById.TryGetValue(n.BusinessId, out var b) ? b : null;
            return new BuyerNotificationDto(
                Id: n.Id,
                BusinessId: n.BusinessId,
                BusinessName: biz?.Name ?? "",
                BrandPrimaryColor: !string.IsNullOrWhiteSpace(biz?.BrandPrimaryColor)
                    ? biz!.BrandPrimaryColor
                    : DefaultBrandColor,
                Title: n.Title,
                Message: n.Message,
                Tag: n.Tag,
                Url: n.Url,
                OrderId: n.OrderId,
                CreatedAt: n.CreatedAt,
                ReadAt: n.ReadAt);
        }).ToList();
    }

    public async Task MarkAsReadAsync(
        int accountId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        var notification = await _db.Notifications.IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.Id == notificationId, cancellationToken);

        if (notification is null)
        {
            throw new NotificationNotFoundException("Esta notificación no existe.");
        }

        // Validar que pertenece a la Account (directo, o vía Client.AccountId).
        var belongs = notification.AccountId == accountId
            || (notification.ClientId is not null
                && await _db.Clients.AsNoTracking().IgnoreQueryFilters()
                    .AnyAsync(c => c.Id == notification.ClientId && c.AccountId == accountId,
                        cancellationToken));

        if (!belongs)
        {
            throw new NotificationNotFoundException("Esta notificación no está en tu cuenta.");
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> MarkAllAsReadAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        // Subquery: IDs de Client de la Account.
        var myClientIds = _db.Clients.IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => (int?)c.Id);

        var unread = await _db.Notifications.IgnoreQueryFilters()
            .Where(n => n.ReadAt == null
                        && (n.AccountId == accountId || myClientIds.Contains(n.ClientId)))
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var n in unread)
        {
            n.ReadAt = now;
        }
        if (unread.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        return unread.Count;
    }

    public async Task<int> CountUnreadAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var myClientIds = _db.Clients.IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => (int?)c.Id);

        return await _db.Notifications.IgnoreQueryFilters()
            .CountAsync(n => n.ReadAt == null
                              && (n.AccountId == accountId || myClientIds.Contains(n.ClientId)),
                cancellationToken);
    }

    private record BizLite(int Id, string Name, string BrandPrimaryColor);
}
