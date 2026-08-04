using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    public class OrderPackage : ITenantOwned
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Negocio (tenant) dueno de este paquete.</summary>
        public int BusinessId { get; set; }

        [Required]
        public int OrderId { get; set; }
        [ForeignKey(nameof(OrderId))]
        public Order Order { get; set; } = null!;

        public int PackageNumber { get; set; }

        /// <summary>Formato de etiqueta que la bolsa conserva como su preferido.</summary>
        public LabelMediaSize MediaSize { get; set; } = LabelMediaSize.Shipping4x6;

        [Required, MaxLength(100)]
        public string QrCodeValue { get; set; } = string.Empty;

        public PackageTrackingStatus Status { get; set; } = PackageTrackingStatus.Packed;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LoadedAt { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public DateTime? ReturnedAt { get; set; }
    }

    public enum PackageTrackingStatus
    {
        Packed = 0,
        Loaded = 1,
        Delivered = 2,
        Returned = 3
    }
}
