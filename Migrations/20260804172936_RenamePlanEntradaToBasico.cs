using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class RenamePlanEntradaToBasico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Datos existentes: "Entrada" pasa a llamarse "Básico".
            migrationBuilder.Sql(
                "UPDATE \"Businesses\" SET \"PlanTier\" = 'Básico' WHERE \"PlanTier\" = 'Entrada';");
            migrationBuilder.Sql(
                "UPDATE \"Businesses\" SET \"PendingPlanTier\" = 'Básico' WHERE \"PendingPlanTier\" = 'Entrada';");

            migrationBuilder.AlterColumn<string>(
                name: "PlanTier",
                table: "Businesses",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Básico",
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldDefaultValue: "Entrada");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PlanTier",
                table: "Businesses",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Entrada",
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldDefaultValue: "Básico");

            // Revertir datos migrados a su valor historico.
            migrationBuilder.Sql(
                "UPDATE \"Businesses\" SET \"PlanTier\" = 'Entrada' WHERE \"PlanTier\" = 'Básico';");
            migrationBuilder.Sql(
                "UPDATE \"Businesses\" SET \"PendingPlanTier\" = 'Entrada' WHERE \"PendingPlanTier\" = 'Básico';");
        }
    }
}
