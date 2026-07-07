using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public enum PayoutAccountKind
{
    Clabe,
    DebitCard,
    BankAccount,
    Phone
}

/// <summary>
/// Cuenta destino para que una clienta pueda pagar por transferencia.
/// El numero se protege con Data Protection desde el DbContext y nunca se
/// devuelve completo a la app.
/// </summary>
public class PayoutAccount : ITenantOwned
{
    [Key]
    public int Id { get; set; }

    public int BusinessId { get; set; }

    public PayoutAccountKind Kind { get; set; } = PayoutAccountKind.Clabe;

    [Required, MaxLength(120)]
    public string HolderName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? BankName { get; set; }

    [MaxLength(80)]
    public string? Alias { get; set; }

    [Required]
    public string AccountNumber { get; set; } = string.Empty;

    [Required, MaxLength(32)]
    public string MaskedNumber { get; set; } = string.Empty;

    public int NumberLength { get; set; }

    [MaxLength(300)]
    public string? Notes { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
