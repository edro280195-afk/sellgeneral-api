using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

/// <summary>
/// Evento de analítica auto-alojada sobre un enlace de pedido compartido
/// (/o/{accessToken}). Reemplaza a GA/Firebase: cada vez que alguien abre el
/// muro, toca "Abrir en la app", descarga de Play/App Store o la app capta el
/// Install Referrer, dejamos un renglón aquí. El dueño del tenant ve sólo sus
/// eventos (query filter de <see cref="ITenantOwned"/>).
/// </summary>
[Table("LinkEvents")]
public class LinkEvent : ITenantOwned
{
    [Key]
    public int Id { get; set; }

    /// <summary>Negocio (tenant) dueño del pedido enlazado.</summary>
    public int BusinessId { get; set; }

    /// <summary>Token público del pedido sobre el que se generó el evento.</summary>
    [Required]
    [MaxLength(80)]
    public string OrderAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Tipo de evento:
    /// <list type="bullet">
    /// <item><c>impression</c> — alguien abrió el muro /o/{token}.</item>
    /// <item><c>open_app</c> — tocó "Abrir en la app" (intent/scheme).</item>
    /// <item><c>store_android</c> — tocó "Descargar para Android".</item>
    /// <item><c>store_ios</c> — tocó "Descargar para iPhone".</item>
    /// <item><c>install_referrer</c> — la app reporta el referrer capturado por
    /// Play Install Referrer API tras una instalación.</item>
    /// </list>
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string Event { get; set; } = "impression";

    /// <summary>
    /// Referrer crudo capturado por Play Install Referrer (p. ej.
    /// <c>token=ABC</c>). Se guarda tal cual para forensic/atrición.
    /// </summary>
    [MaxLength(512)]
    public string? Referrer { get; set; }

    /// <summary>User-Agent del navegador/app que generó el evento.</summary>
    [MaxLength(512)]
    public string? UserAgent { get; set; }

    /// <summary>IP de origen (para detectar bots/sosp de fraude de clics).</summary>
    [MaxLength(64)]
    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
