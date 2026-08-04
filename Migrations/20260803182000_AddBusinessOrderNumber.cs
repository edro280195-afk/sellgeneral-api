using EntregasApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260803182000_AddBusinessOrderNumber")]
    public partial class AddBusinessOrderNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Esta migracion se publico originalmente sin metadatos de EF y algunos
            // ambientes pueden tener una parte del cambio aplicada manualmente.
            migrationBuilder.Sql("""
                ALTER TABLE "Orders"
                ADD COLUMN IF NOT EXISTS "OrderNumber" integer NOT NULL DEFAULT 0;
                """);

            migrationBuilder.Sql("""
                UPDATE "Orders" AS o
                SET "OrderNumber" = ranked."OrderNumber"
                FROM (
                    SELECT
                        "Id",
                        ROW_NUMBER() OVER (
                            PARTITION BY "BusinessId"
                            ORDER BY "CreatedAt", "Id"
                        )::integer AS "OrderNumber"
                    FROM "Orders"
                ) AS ranked
                WHERE o."Id" = ranked."Id";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Orders"
                ALTER COLUMN "OrderNumber" SET DEFAULT 0,
                ALTER COLUMN "OrderNumber" SET NOT NULL;

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Orders_BusinessId_OrderNumber"
                ON "Orders" ("BusinessId", "OrderNumber");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_Orders_BusinessId_OrderNumber";
                ALTER TABLE "Orders" DROP COLUMN IF EXISTS "OrderNumber";
                """);
        }
    }
}
