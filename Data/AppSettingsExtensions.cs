using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Data;

public static class AppSettingsExtensions
{
    // Valores por defecto de un negocio nuevo (mismos que usa el seeder / migrador).
    private const decimal DefaultShippingCost = 60m;
    private const int DefaultLinkExpirationHours = 72;

    /// <summary>
    /// Devuelve la configuración (AppSettings) del tenant activo, creándola con valores por
    /// defecto si todavía no existe. Antes el código hacía <c>AppSettings.FirstAsync()</c>
    /// (era single-tenant, donde la fila Id=1 siempre estaba sembrada); en multi-tenant un
    /// negocio recién creado puede no tener fila aún y eso reventaba con
    /// "Sequence contains no elements". Esto lo hace robusto: lazy-init por tenant.
    /// </summary>
    public static async Task<AppSettings> GetOrCreateTenantSettingsAsync(
        this AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        var settings = await db.AppSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new AppSettings
        {
            DefaultShippingCost = DefaultShippingCost,
            LinkExpirationHours = DefaultLinkExpirationHours
        };
        // El BusinessId lo estampa SaveChanges (StampTenantOwnedEntities) con el tenant activo.
        db.AppSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }
}
