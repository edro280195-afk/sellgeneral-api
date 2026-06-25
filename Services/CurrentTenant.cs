using Microsoft.Extensions.Configuration;

namespace EntregasApi.Services;

/// <summary>
/// Expone el negocio activo de la petición actual. En el bloque 0.1 hace default a
/// Business #1 para no romper el comportamiento single-tenant; en 0.2 el middleware de
/// auth llamará a <see cref="SetBusiness"/> con el negocio validado contra las Memberships
/// del caller (header X-Business-Id / claim del JWT).
/// </summary>
public interface ICurrentTenant
{
    int ActiveBusinessId { get; }
    bool IsResolved { get; }
    void SetBusiness(int businessId);
}

public class CurrentTenant : ICurrentTenant
{
    private readonly int _defaultBusinessId;
    private int? _businessId;

    public CurrentTenant(IConfiguration config)
    {
        // Configurable por si se quiere otro default en DEV; cae a 1 (Regi Bazar = Tenant #1).
        _defaultBusinessId = config.GetValue<int?>("Tenant:DefaultBusinessId") ?? 1;
    }

    public int ActiveBusinessId => _businessId ?? _defaultBusinessId;

    public bool IsResolved => _businessId.HasValue;

    public void SetBusiness(int businessId) => _businessId = businessId;
}
