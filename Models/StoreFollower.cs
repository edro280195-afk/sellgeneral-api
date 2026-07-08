using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Relación "seguir tienda" entre una Account global (compradora) y un
/// Business (tenant). Distinta de <see cref="Client"/>: una compradora
/// puede seguir una tienda sin haberle comprado nunca. El campo
/// <see cref="IsVip"/> vive en esta misma fila (no en tabla aparte) porque
/// hoy VIP es solo "la vendedora marca a una seguidora ya existente", sin
/// niveles ni vigencia propia.
/// </summary>
public class StoreFollower : ITenantOwned
{
    public int Id { get; set; }

    /// <summary>Negocio (tenant) que es seguido.</summary>
    public int BusinessId { get; set; }

    /// <summary>Account global (compradora) que sigue la tienda.</summary>
    public int AccountId { get; set; }

    public bool NotifyOnPost { get; set; } = true;

    public bool NotifyOnLive { get; set; } = true;

    public bool IsVip { get; set; } = false;

    public DateTime? VipSince { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null = sigue activamente. No-null = dejó de seguir (soft-unfollow).</summary>
    public DateTime? UnfollowedAt { get; set; }
}
