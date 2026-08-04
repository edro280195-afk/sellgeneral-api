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

    /// <summary>Momento UTC en que aceptó términos y aviso de privacidad.</summary>
    public DateTime? LegalAcceptedAtUtc { get; set; }

    /// <summary>Versión legal aceptada, normalmente la fecha publicada en la landing.</summary>
    [MaxLength(32)]
    public string? LegalVersion { get; set; }

    /// <summary>Momento en que la clienta termino el recorrido inicial de la app.</summary>
    public DateTime? BuyerOnboardingCompletedAtUtc { get; set; }

    /// <summary>Momento en que la vendedora termino el recorrido inicial de su negocio.</summary>
    public DateTime? SellerOnboardingCompletedAtUtc { get; set; }

    /// <summary>
    /// Primera y unica concesion de prueba para esta identidad. No se limpia al
    /// cancelar, vencer o eliminar un negocio.
    /// </summary>
    public DateTime? SellerTrialGrantedAtUtc { get; set; }

    /// <summary>Ultima vez que se evaluo la elegibilidad para una prueba.</summary>
    public DateTime? SellerTrialEvaluatedAtUtc { get; set; }

    /// <summary>
    /// Huella HMAC del identificador aleatorio de instalacion. Nunca se guarda
    /// el identificador crudo enviado por la app.
    /// </summary>
    [MaxLength(64)]
    public string? SellerTrialDeviceHash { get; set; }

    /// <summary>Motivo interno y estable cuando la prueba no fue concedida.</summary>
    [MaxLength(40)]
    public string? SellerTrialRestrictionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navegación
    public ICollection<Membership> Memberships { get; set; } = new List<Membership>();
}
