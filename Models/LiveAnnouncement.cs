using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Aviso en tiempo real de "estoy en vivo ahora" — la vendedora lo crea al
/// tocar un botón justo cuando empieza a transmitir (normalmente en
/// Facebook). Deliberadamente separado de <see cref="LiveSession"/>, que es
/// el pipeline post-hoc de transcripción de un live YA grabado
/// (<c>FacebookUrl</c> obligatorio, máquina de estados de descarga/
/// transcripción) — mezclarlos rompería esa semántica.
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
}
