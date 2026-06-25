using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    public class DriverExpense : ITenantOwned
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Negocio (tenant) dueno de este gasto.</summary>
        public int BusinessId { get; set; }

        [Required]
        public int? DeliveryRouteId { get; set; }
        [ForeignKey(nameof(DeliveryRouteId))]
        public DeliveryRoute? DeliveryRoute { get; set; }
        [Column(TypeName = "decimal(12,2)")]
        public decimal Amount { get; set; }

        [Required, MaxLength(50)]
        public string ExpenseType { get; set; } = "Gasolina";

        public DateTime Date { get; set; } = DateTime.Now;

        [MaxLength(500)]
        public string? Notes { get; set; }

        [MaxLength(500)]
        public string? EvidencePath { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
