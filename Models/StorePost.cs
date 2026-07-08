using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Publicación/novedad de la tienda (texto + foto opcional) que ven sus
/// seguidoras en la pantalla de Tienda. <see cref="IsVipOnly"/> solo puede
/// activarse si el plan de la tienda incluye <c>Feature.VipDrops</c>
/// (validado en el controller, no aquí).
/// </summary>
public class StorePost : ITenantOwned
{
    public int Id { get; set; }

    public int BusinessId { get; set; }

    [Required, MaxLength(1000)]
    public string Body { get; set; } = "";

    [MaxLength(500)]
    public string? ImageUrl { get; set; }

    public bool IsVipOnly { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft delete — se conserva para auditoría.</summary>
    public DateTime? DeletedAt { get; set; }
}
