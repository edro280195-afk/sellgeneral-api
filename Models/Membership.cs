using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Rol de una persona dentro de un negocio. El rol es POR RELACIÓN, no global:
/// "soy Owner de éste y clienta de otros" vive aquí.
/// </summary>
public enum MembershipRole
{
    Owner = 0,
    Admin = 1,
    Driver = 2,
    Scaner = 3
}

/// <summary>
/// La RELACIÓN persona↔negocio con su rol. Unique (AccountId, BusinessId): una persona
/// tiene a lo sumo una membership por negocio.
/// </summary>
public class Membership
{
    [Key]
    public int Id { get; set; }

    public int AccountId { get; set; }
    public Account? Account { get; set; }

    public int BusinessId { get; set; }
    public Business? Business { get; set; }

    public MembershipRole Role { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
