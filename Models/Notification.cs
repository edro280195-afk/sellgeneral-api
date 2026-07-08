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

    /// <summary>
    /// Client al que se dirige la notificación. Null cuando la notificación
    /// viene de un seguimiento (<see cref="StoreFollower"/>) y la compradora
    /// no tiene Client en este negocio — en ese caso se usa
    /// <see cref="AccountId"/> para resolverla en el historial.
    /// </summary>
    public int? ClientId { get; set; }

    /// <summary>
    /// Account global a la que se dirige, cuando no hay Client (seguidora
    /// sin compra en este negocio). Las notificaciones viejas por pedido
    /// siguen resolviéndose vía ClientId -&gt; Client.AccountId.
    /// </summary>
    public int? AccountId { get; set; }

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
