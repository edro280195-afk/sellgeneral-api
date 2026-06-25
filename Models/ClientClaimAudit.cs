using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public enum ClientClaimMode
{
    OrderToken = 0,
    PhoneMatch = 1,
    Manual = 2,
}

/// <summary>
/// Auditoría de "reclamar perfil" (plan 0.3): una Account se enlazó con un Client.
/// Una Account puede tener N Client (una por vendedora); cada enlace queda registrado
/// para diagnóstico y para detectar suplantación.
/// </summary>
public class ClientClaimAudit
{
    [Key]
    public int Id { get; set; }

    /// <summary>Persona que reclamó el Client (Account.Id).</summary>
    public int AccountId { get; set; }

    /// <summary>Registro de clienta que se enlazó (Client.Id).</summary>
    public int ClientId { get; set; }

    /// <summary>Negocio (tenant) dueño del Client. Se desnormaliza para análisis rápidos.</summary>
    public int BusinessId { get; set; }

    public ClientClaimMode Mode { get; set; }

    /// <summary>Detalle legible: p.ej. "order token Order#118", "phone match +52...".</summary>
    [MaxLength(300)]
    public string? Reason { get; set; }

    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
}
