using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Token de push (FCM) de un dispositivo de la app nativa de la compradora.
/// Deliberadamente NO es <see cref="ITenantOwned"/>: una compradora sigue N
/// tiendas con el mismo dispositivo, así que el token se resuelve por
/// <see cref="AccountId"/> (igual que <see cref="Client"/>/<see cref="Notification"/>
/// se resuelven cross-tenant), no por negocio. Distinto de
/// <see cref="FcmToken"/>, que es por-tenant y sirve a la app nativa de
/// choferes/admin.
/// </summary>
public class BuyerDeviceToken
{
    public int Id { get; set; }

    public int AccountId { get; set; }

    [Required, MaxLength(512)]
    public string Token { get; set; } = string.Empty;

    /// <summary>"android" | "ios"</summary>
    [Required, MaxLength(20)]
    public string Platform { get; set; } = "android";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
