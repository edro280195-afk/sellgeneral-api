using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Aviso en tiempo real de "estoy en vivo ahora" — la vendedora lo crea al
/// tocar un botón justo cuando empieza a transmitir (normalmente en
/// Facebook). También carga qué producto está anunciando en este momento
/// (<see cref="LiveHub.AnnounceProduct"/> actualiza los campos Current*),
/// para que una compradora que abre la app a mitad del vivo vea de inmediato
/// lo último anunciado en vez de esperar al siguiente evento de SignalR.
/// </summary>
public class LiveAnnouncement : ITenantOwned
{
    public int Id { get; set; }

    public int BusinessId { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null = sigue activo (ver TTL en el service: 3h desde StartedAt).</summary>
    public DateTime? EndedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Producto anunciado ahora mismo (nulo = nada anunciado todavía) ──

    public int? CurrentProductId { get; set; }

    [MaxLength(200)]
    public string? CurrentProductName { get; set; }

    public decimal? CurrentProductPrice { get; set; }

    public DateTime? CurrentAnnouncedAt { get; set; }
}
