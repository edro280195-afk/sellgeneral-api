using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

/// <summary>
/// Registro histórico de una fusión de pedidos (ver OrdersController.MergeOrders). Se
/// conserva aunque el pedido de origen quede como cascarón Cancelado, para poder responder
/// "¿qué le pasó al pedido #X?" o "¿de dónde salió este artículo?" tiempo después.
/// </summary>
public class OrderMergeAudit : ITenantOwned
{
    [Key]
    public int Id { get; set; }

    /// <summary>Negocio (tenant) dueno de esta fusión.</summary>
    public int BusinessId { get; set; }

    public int SourceOrderId { get; set; }
    public int SourceClientId { get; set; }
    [Required, MaxLength(200)] public string SourceClientName { get; set; } = "";

    public int TargetOrderId { get; set; }
    public int TargetClientId { get; set; }
    [Required, MaxLength(200)] public string TargetClientName { get; set; } = "";

    public int ItemsMoved { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal AmountMoved { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal PaymentsMoved { get; set; }

    public DateTime MergedAt { get; set; } = DateTime.UtcNow;
}
