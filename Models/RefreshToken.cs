using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

/// <summary>
/// Refresh token opaco y rotativo para mantener la sesión sin re-autenticar.
/// Se guarda SOLO el hash SHA-256 del token; el valor crudo se entrega una vez
/// a la app. En cada uso se rota (revoca el viejo, emite uno nuevo). Si se reusa
/// uno ya revocado se asume robo y se revocan todos los de la cuenta.
/// </summary>
public class RefreshToken
{
    [Key]
    public int Id { get; set; }

    public int AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>SHA-256 (hex) del token crudo. Nunca se persiste el valor crudo.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    /// <summary>Hash del token que reemplazó a este (auditoría de rotación).</summary>
    [MaxLength(64)]
    public string? ReplacedByHash { get; set; }

    [NotMapped]
    public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
}
