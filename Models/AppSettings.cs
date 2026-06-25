namespace EntregasApi.Models;

public class AppSettings : ITenantOwned
{
    [System.ComponentModel.DataAnnotations.Key]
    public int Id { get; set; }

    /// <summary>Negocio (tenant) dueño de esta config. Una fila por Business (ya no es singleton Id=1).</summary>
    public int BusinessId { get; set; }

    /// <summary>Costo de envío por defecto en MXN</summary>
    public decimal DefaultShippingCost { get; set; } = 60m;

    /// <summary>Horas de vigencia del enlace de la clienta</summary>
    public int LinkExpirationHours { get; set; } = 72;
}
