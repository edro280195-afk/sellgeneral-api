using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

/// <summary>
/// Notificación persistida para la app de la compradora. Se crea cada
/// vez que <see cref="EntregasApi.Services.IPushNotificationService.SendNotificationToClientAsync"/>
/// emite un push a una clienta. La app consume el historial vía
/// <c>GET /api/me/notifications</c> (cross-tenant por AccountId).
/// </summary>
public class Notification : ITenantOwned
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Negocio (tenant) dueño de la notificación.</summary>
    public int BusinessId { get; set; }

    /// <summary>Client al que se dirige la notificación.</summary>
    public int ClientId { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Tag semántico (e.g. "delivered", "driver-en-route", "driver-nearby",
    /// "chat-driver", "order-confirmed", "card-payment", "reserve").
    /// La app lo usa para decidir icono/cta/animación.
    /// </summary>
    [Required, MaxLength(50)]
    public string Tag { get; set; } = string.Empty;

    /// <summary>URL de deep link opcional (e.g. /tracking/123?token=xxx).</summary>
    [MaxLength(500)]
    public string? Url { get; set; }

    /// <summary>Pedido relacionado (opcional).</summary>
    public int? OrderId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cuándo la compradora marcó la notificación como leída. Null = no leída.</summary>
    public DateTime? ReadAt { get; set; }
}
