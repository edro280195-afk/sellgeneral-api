using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// El TENANT: cada negocio (vendedora) que usa la plataforma. Multi-tenancy gira
/// alrededor de esta entidad. Todas las entidades de negocio cuelgan de un Business
/// vía BusinessId (ver <see cref="ITenantOwned"/>).
/// </summary>
public class Business
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Identificador corto y único para URLs y carpetas de storage (ej. "regibazar").</summary>
    [Required, MaxLength(60)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? City { get; set; }

    /// <summary>Dominio público del frontend de este negocio (para CORS y links). Parametriza App:FrontendUrl.</summary>
    [MaxLength(300)]
    public string? FrontendUrl { get; set; }

    /// <summary>URL del logo de la tienda (subido a Cloudinary en "{slug}/brand").</summary>
    [MaxLength(500)]
    public string? LogoUrl { get; set; }

    /// <summary>URL del banner de la tienda (subido a Cloudinary en "{slug}/brand").</summary>
    [MaxLength(500)]
    public string? BannerUrl { get; set; }

    /// <summary>Color primario de la marca (hex "#RRGGBB"). Disponible en TODOS los planes.</summary>
    [Required, MaxLength(7)]
    public string BrandPrimaryColor { get; set; } = "#6C4AE0";

    /// <summary>Color de acento opcional de la marca (hex "#RRGGBB").</summary>
    [MaxLength(7)]
    public string? BrandAccentColor { get; set; }

    /// <summary>URL de Messenger para contacto de la clienta (ej. https://m.me/mi.tienda).</summary>
    [MaxLength(300)]
    public string? MessengerUrl { get; set; }

    /// <summary>URL de Facebook del negocio (ej. https://www.facebook.com/mi.tienda).</summary>
    [MaxLength(300)]
    public string? FacebookUrl { get; set; }

    // Depot / centro de ruta de este negocio (reemplaza el hardcode Cami:RouteCenterLat/Lng).
    public double DepotLat { get; set; }
    public double DepotLng { get; set; }

    /// <summary>Bias de región para el geocoding (reemplaza el hardcode "Nuevo Laredo, Tamaulipas").</summary>
    [Required, MaxLength(120)]
    public string GeocodingRegion { get; set; } = "Nuevo Laredo, Tamaulipas, MX";

    /// <summary>Nombre del negocio embebido en los prompts de Gemini (reemplaza el hardcode "Regi Bazar").</summary>
    [MaxLength(150)]
    public string? GeminiBusinessName { get; set; }

    /// <summary>
    /// Access token de Mercado Pago de la VENDEDORA (per-tenant, para cobrar a SUS clientas).
    /// Se guarda ENCRIPTADO en la base (ValueConverter con Data Protection en el DbContext).
    /// NO confundir con las credenciales de PLATAFORMA (cobro de la suscripción, fase 1.3).
    /// </summary>
    public string? MercadoPagoAccessToken { get; set; }

    [MaxLength(200)]
    public string? MercadoPagoPublicKey { get; set; }

    [Required, MaxLength(40)]
    public string PlanTier { get; set; } = "Entrada";

    [MaxLength(40)]
    public string? PendingPlanTier { get; set; }

    public DateTime? PendingPlanEffectiveAt { get; set; }

    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Active;

    public DateTime? TrialEndsAt { get; set; }

    public DateTime? CurrentPeriodEndsAt { get; set; }

    /// <summary>
    /// ID del preapproval (suscripcion) en Mercado Pago creado con las
    /// credenciales de PLATAFORMA. Es lo que la webhook de MP referencia
    /// para notificarnos cobros recurrentes. Null = sin suscripcion
    /// (trial fresco o cuenta bloqueada).
    /// </summary>
    [MaxLength(80)]
    public string? PreapprovalId { get; set; }

    /// <summary>Email del pagador asociado al preapproval de MP.</summary>
    [MaxLength(180)]
    public string? PayerEmail { get; set; }

    /// <summary>Periodicidad actual: 1 (mensual), 3 (trimestral) o 12 (anual) meses.</summary>
    public int SubscriptionPeriodMonths { get; set; } = 1;

    /// <summary>Ultimo status conocido del preapproval segun MP (authorized/paused/cancelled).</summary>
    [MaxLength(40)]
    public string? PreapprovalStatus { get; set; }

    /// <summary>
    /// Cuando el owner cancela la suscripcion, MP mantiene la suscripcion
    /// activa hasta <see cref="CurrentPeriodEndsAt"/>. Aqui guardamos ese
    /// horizonte para distinguir una cancelacion "amigable" (currentPeriodEndsAt &gt; now)
    /// de un cobro fallido.
    /// </summary>
    public DateTime? CancellationEffectiveAt { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navegación
    public ICollection<Membership> Memberships { get; set; } = new List<Membership>();
}
