using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IBuyerNotificationService
{
    /// <summary>
    /// Lista las notificaciones de la cuenta, cross-tenant por AccountId,
    /// ordenadas por fecha descendente. <paramref name="audience"/> separa los
    /// avisos de clienta (<see cref="NotificationAudience.Buyer"/>) de los de
    /// dueña/administradora (<see cref="NotificationAudience.Seller"/>); sin
    /// valor devuelve ambos (compatibilidad con versiones anteriores de la app).
    /// Lanza <see cref="InvalidNotificationAudienceException"/> si el valor no existe.
    /// </summary>
    Task<List<BuyerNotificationDto>> GetMyNotificationsAsync(
        int accountId,
        CancellationToken cancellationToken = default,
        string? audience = null);

    /// <summary>
    /// Marca una notificación como leída. Lanza
    /// <see cref="NotificationNotFoundException"/> si no existe o no es visible
    /// para la Account (no se distingue para no revelar ids ajenos).
    /// </summary>
    Task MarkAsReadAsync(
        int accountId,
        Guid notificationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca como leídas las notificaciones de la cuenta del destinatario
    /// indicado (o todas si no se indica). Devuelve la cantidad actualizada.
    /// </summary>
    Task<int> MarkAllAsReadAsync(
        int accountId,
        CancellationToken cancellationToken = default,
        string? audience = null);

    /// <summary>
    /// Cuenta las notificaciones no leídas del destinatario indicado. Se usa
    /// para el badge en el Home (icono 🔔 con punto rojo).
    /// </summary>
    Task<int> CountUnreadAsync(
        int accountId,
        CancellationToken cancellationToken = default,
        string? audience = null);
}

public class NotificationNotFoundException : Exception
{
    public NotificationNotFoundException(string message) : base(message) { }
}

/// <summary>El destinatario pedido no es "buyer" ni "seller". Se traduce a 400.</summary>
public class InvalidNotificationAudienceException : Exception
{
    public InvalidNotificationAudienceException(string? audience)
        : base($"El destinatario '{audience}' no es válido. Usa 'buyer' o 'seller'.") { }
}

/// <summary>
/// Notificaciones vistas por la persona en la app. Persistidas cada vez que el
/// backend emite un push (clienta, seguidoras o dueñas). La pantalla Home las
/// consulta para el badge de no leídas y la pantalla de Notificaciones las lista.
///
/// Reglas de visibilidad (todas pasan por <see cref="VisibleTo"/>):
///   • Solo las de la propia cuenta: directas por AccountId o vía un Client
///     enlazado a la cuenta.
///   • Los avisos de dueña/administradora (<see cref="NotificationAudience.Seller"/>)
///     solo se ven mientras la cuenta siga siendo Owner/Admin de ese negocio: si la
///     sacan del equipo, deja de ver los avisos de la tienda (nombres, saldos).
///   • Con <c>audience</c> se separa el historial de clienta del de dueña, para que
///     una cuenta con ambos papeles nunca los mezcle.
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
        CancellationToken cancellationToken = default,
        string? audience = null)
    {
        var rows = await VisibleTo(accountId, NormalizeAudience(audience))
            .AsNoTracking()
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new
            {
                n.Id,
                n.BusinessId,
                n.ClientId,
                n.Audience,
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
                ReadAt: n.ReadAt,
                Audience: n.Audience);
        }).ToList();
    }

    public async Task MarkAsReadAsync(
        int accountId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        // La misma regla de visibilidad que el listado: si no la ves, no existe.
        var notification = await VisibleTo(accountId, audience: null)
            .FirstOrDefaultAsync(n => n.Id == notificationId, cancellationToken);

        if (notification is null)
        {
            throw new NotificationNotFoundException("Esta notificación no existe.");
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> MarkAllAsReadAsync(
        int accountId,
        CancellationToken cancellationToken = default,
        string? audience = null)
    {
        var unread = await VisibleTo(accountId, NormalizeAudience(audience))
            .Where(n => n.ReadAt == null)
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
        CancellationToken cancellationToken = default,
        string? audience = null)
    {
        return await VisibleTo(accountId, NormalizeAudience(audience))
            .CountAsync(n => n.ReadAt == null, cancellationToken);
    }

    /// <summary>
    /// Notificaciones que esta cuenta puede ver. Única fuente de la regla de
    /// visibilidad: listado, contador, "marcar todas" y "marcar una" la usan.
    /// </summary>
    private IQueryable<Notification> VisibleTo(int accountId, string? audience)
    {
        // IDs de Client enlazados a la cuenta (las notificaciones por pedido
        // solo guardan ClientId).
        var myClientIds = _db.Clients.IgnoreQueryFilters()
            .Where(c => c.AccountId == accountId)
            .Select(c => (int?)c.Id);

        var query = _db.Notifications.IgnoreQueryFilters()
            .Where(n => n.AccountId == accountId || myClientIds.Contains(n.ClientId))
            // Avisos de la tienda: solo mientras siga siendo dueña/administradora.
            .Where(n => n.Audience != NotificationAudience.Seller
                        || _db.Memberships.Any(m => m.AccountId == accountId
                                                    && m.BusinessId == n.BusinessId
                                                    && (m.Role == MembershipRole.Owner
                                                        || m.Role == MembershipRole.Admin)));

        return audience is null
            ? query
            : query.Where(n => n.Audience == audience);
    }

    /// <summary>null/vacío = sin filtro; "buyer"/"seller" = filtro; otro valor = error.</summary>
    private static string? NormalizeAudience(string? audience)
    {
        if (string.IsNullOrWhiteSpace(audience)) return null;

        var normalized = audience.Trim().ToLowerInvariant();
        if (!NotificationAudience.IsValid(normalized))
        {
            throw new InvalidNotificationAudienceException(audience);
        }
        return normalized;
    }

    private record BizLite(int Id, string Name, string BrandPrimaryColor);
}
