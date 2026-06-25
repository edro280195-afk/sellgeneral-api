using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    [Migration("20260624225200_AddMissingOrderScheduledDeliveryDateColumn")]
    public partial class AddMissingOrderScheduledDeliveryDateColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Orders"
                ADD COLUMN IF NOT EXISTS "ScheduledDeliveryDate" timestamp with time zone NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op intencional: esta reparacion evita romper bases donde la columna ya existia.
        }
    }
}
