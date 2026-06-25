using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public class CashRegisterSession : ITenantOwned
{
    [Key]
    public int Id { get; set; }

    /// <summary>Negocio (tenant) dueño de esta caja.</summary>
    public int BusinessId { get; set; }

    [Required]
    public int AccountId { get; set; }
    [ForeignKey(nameof(AccountId))]
    public Account Account { get; set; } = null!;

    public DateTime OpeningTime { get; set; } = DateTime.UtcNow;
    
    public DateTime? ClosingTime { get; set; }

    [Column(TypeName = "decimal(12,2)")]
    public decimal InitialCash { get; set; }

    [Column(TypeName = "decimal(12,2)")]
    public decimal FinalCashExpected { get; set; }

    [Column(TypeName = "decimal(12,2)")]
    public decimal? FinalCashActual { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Open;

    public ICollection<OrderPayment> Payments { get; set; } = new List<OrderPayment>();
}

public enum SessionStatus
{
    Open = 0,
    Closed = 1
}
