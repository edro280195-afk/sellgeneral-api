using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    [Migration("20260624224500_AddMissingLegacyUserRoleColumn")]
    public partial class AddMissingLegacyUserRoleColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Users"
                ADD COLUMN IF NOT EXISTS "Rol" text NOT NULL DEFAULT 'Admin';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op intencional: esta reparacion evita romper bases donde "Rol" ya existia.
        }
    }
}
