using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// La PERSONA: identidad global única por humano, compartida entre todos los negocios.
/// Una misma persona puede ser Owner de un negocio y clienta de otros (ver <see cref="Membership"/>).
/// Debe tener al menos un método de identidad presente (Phone, FacebookUserId o Email) — se
/// garantiza con un CHECK constraint en el DbContext.
/// </summary>
public class Account
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Nombre de pila de la compradora (registro por teléfono). Opcional para cuentas legacy.</summary>
    [MaxLength(100)]
    public string? FirstName { get; set; }

    /// <summary>Apellido de la compradora (registro por teléfono). Opcional para cuentas legacy.</summary>
    [MaxLength(100)]
    public string? LastName { get; set; }

    [MaxLength(500)]
    public string? ProfilePhotoUrl { get; set; }

    /// <summary>Teléfono normalizado (solo dígitos, ver TextNormalizer). Unique cuando no es null.</summary>
    [MaxLength(20)]
    public string? Phone { get; set; }

    /// <summary>
    /// Momento en que la compradora confirmó su teléfono por WhatsApp. Null = sin verificar.
    /// El login por teléfono+contraseña exige que este campo no sea null.
    /// </summary>
    public DateTime? PhoneVerifiedAt { get; set; }

    /// <summary>Id app-scoped de Facebook Login (public_profile). Unique cuando no es null.</summary>
    [MaxLength(100)]
    public string? FacebookUserId { get; set; }

    [MaxLength(150)]
    public string? Email { get; set; }

    /// <summary>Hash BCrypt. Solo para cuentas legacy (admin/conductor migradas desde User).</summary>
    public string? PasswordHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navegación
    public ICollection<Membership> Memberships { get; set; } = new List<Membership>();
}
