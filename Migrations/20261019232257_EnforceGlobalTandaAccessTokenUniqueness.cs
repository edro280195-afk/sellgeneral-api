using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class EnforceGlobalTandaAccessTokenUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Businesses"
                SET "FrontendUrl" = 'https://app.nenisapp.com'
                WHERE "FrontendUrl" IS NOT NULL
                  AND LOWER(TRIM("FrontendUrl")) ~ '^https?://([a-z0-9-]+\.)*regibazar\.com(:[0-9]+)?(/.*)?$';
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM tandas
                        GROUP BY access_token
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Hay tokens de tanda duplicados; se requiere resolverlos antes de crear el índice global.';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_tandas_access_token",
                table: "tandas",
                column: "access_token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tandas_access_token",
                table: "tandas");

            // No se restauran dominios legacy para evitar reactivar enlaces antiguos.
        }
    }
}
