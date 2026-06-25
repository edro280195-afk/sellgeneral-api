using EntregasApi.DTOs;
using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public class Client : ITenantOwned
{
    [Key]
    public int Id { get; set; }

    /// <summary>Negocio (tenant) dueño de este registro de clienta.</summary>
    public int BusinessId { get; set; }

    /// <summary>
    /// Account global enlazada cuando la persona reclamó su perfil (null = clienta anónima
    /// creada por la vendedora, estado normal). Una Account ↔ muchas Client (una por vendedora). Ver 0.3.
    /// </summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(500)]
    public string? FacebookProfileUrl { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "Nueva";
    public ClientTag Tag { get; set; } = ClientTag.None;
    public int CurrentPoints { get; set; } = 0;
    public int LifetimePoints { get; set; } = 0;
    public string? DeliveryInstructions { get; set; }

    // Identidad normalizada para fuzzy matching y resolución multi-señal
    public string NormalizedName { get; set; } = string.Empty;
    public string? NormalizedPhone { get; set; }
    public string? NormalizedAddress { get; set; }

    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<ClientAlias> Aliases { get; set; } = new List<ClientAlias>();
}
