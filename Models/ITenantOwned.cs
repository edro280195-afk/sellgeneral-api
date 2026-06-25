namespace EntregasApi.Models;

/// <summary>
/// Marca una entidad como propiedad de un tenant (Business). El DbContext aplica un
/// HasQueryFilter global por BusinessId == ICurrentTenant.ActiveBusinessId a todas las
/// entidades que implementan esta interfaz. Las raíces de agregado y las hijas con
/// endpoint propio llevan BusinessId (denormalizado en las hijas para poder filtrar sin joins).
/// </summary>
public interface ITenantOwned
{
    int BusinessId { get; set; }
}
