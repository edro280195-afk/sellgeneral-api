using Npgsql;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Transformacion A del plan: crear Business Id=1 (no existe en el origen) con la
/// identidad real de Regi Bazar. Estampa <see cref="MigrationPlan.BusinessId"/>.
///
/// MercadoPagoAccessToken se recibe ya encriptado (TokenEncryptor) o null. Si es null
/// se registra una advertencia.
/// </summary>
public sealed class BusinessSeeder
{
    public async Task SeedAsync(
        NpgsqlConnection dest,
        NpgsqlTransaction destTx,
        string? encryptedMpToken,
        bool tokenWasProvided,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO "Businesses"
                ("Id", "Name", "Slug", "City", "FrontendUrl",
                 "LogoUrl", "BannerUrl", "BrandPrimaryColor", "BrandAccentColor",
                 "DepotLat", "DepotLng", "GeocodingRegion", "GeminiBusinessName",
                 "MercadoPagoAccessToken", "PlanTier", "SubscriptionStatus",
                 "TrialEndsAt", "CurrentPeriodEndsAt", "SubscriptionPeriodMonths",
                 "IsActive", "CreatedAt")
            VALUES
                (@Id, @Name, @Slug, @City, @FrontendUrl,
                 NULL, NULL, @BrandPrimaryColor, NULL,
                 @DepotLat, @DepotLng, @GeocodingRegion, @GeminiBusinessName,
                 @MpToken, @PlanTier, @SubscriptionStatus,
                 NULL, NULL, 1,
                 TRUE, NOW())
            ON CONFLICT ("Id") DO NOTHING
            """;

        await using var cmd = new NpgsqlCommand(sql, dest, destTx);
        cmd.Parameters.AddWithValue("Id", MigrationPlan.BusinessId);
        cmd.Parameters.AddWithValue("Name", "Regi Bazar");
        cmd.Parameters.AddWithValue("Slug", "regibazar");
        cmd.Parameters.AddWithValue("City", "Nuevo Laredo");
        cmd.Parameters.AddWithValue("FrontendUrl", "https://regibazar.com");
        cmd.Parameters.AddWithValue("BrandPrimaryColor", "#FF0072");
        cmd.Parameters.AddWithValue("DepotLat", 27.4861);
        cmd.Parameters.AddWithValue("DepotLng", -99.5069);
        cmd.Parameters.AddWithValue("GeocodingRegion", "Nuevo Laredo, Tamaulipas, MX");
        cmd.Parameters.AddWithValue("GeminiBusinessName", "Regi Bazar");
        cmd.Parameters.AddWithValue("MpToken", (object?)encryptedMpToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("PlanTier", "Elite");
        cmd.Parameters.AddWithValue("SubscriptionStatus", "Active");
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (!tokenWasProvided)
        {
            // Advertencia (la loggea el runner principal, no el seeder)
        }
    }
}
