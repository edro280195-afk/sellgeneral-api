using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

/// <summary>
/// Evaluación que la clienta deja al momento de la entrega (flujo Nenis V3).
/// Una evaluación por pedido: unique (OrderId). Si la clienta salta la pantalla,
/// no se registra fila (null-safe en el GET de ClientOrderView).
/// </summary>
public class OrderRating : ITenantOwned
{
    [Key]
    public int Id { get; set; }

    /// <summary>Negocio (tenant) dueño de esta evaluación.</summary>
    public int BusinessId { get; set; }

    [Required]
    public int OrderId { get; set; }

    [ForeignKey(nameof(OrderId))]
    public Order Order { get; set; } = null!;

    /// <summary>Estrellas 1–5.</summary>
    [Range(1, 5)]
    public int Stars { get; set; }

    /// <summary>Motivos seleccionados (stickers), serializado como JSON array.</summary>
    [MaxLength(500)]
    public string? Reasons { get; set; }

    /// <summary>Comentario libre de la clienta (opcional).</summary>
    [MaxLength(1000)]
    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
