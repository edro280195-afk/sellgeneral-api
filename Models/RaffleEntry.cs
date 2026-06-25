using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("raffle_entries")]
public class RaffleEntry : ITenantOwned
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>Negocio (tenant) dueno de esta entrada.</summary>
    [Column("business_id")]
    public int BusinessId { get; set; }

    [Required]
    [Column("raffle_id")]
    public Guid RaffleId { get; set; }

    [Required]
    [Column("client_id")]
    public int ClientId { get; set; }

    [Required]
    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("entered_at")]
    public DateTime EnteredAt { get; set; } = DateTime.UtcNow;

    // Relaciones
    [ForeignKey(nameof(RaffleId))]
    public Raffle Raffle { get; set; } = null!;

    [ForeignKey(nameof(ClientId))]
    public Client Client { get; set; } = null!;

    [ForeignKey(nameof(OrderId))]
    public Order Order { get; set; } = null!;
}
