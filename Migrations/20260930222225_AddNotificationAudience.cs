using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Audience",
                table: "Notifications",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "buyer");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_AccountId_Audience",
                table: "Notifications",
                columns: new[] { "AccountId", "Audience" });

            // Los avisos periódicos de la dueña (AdminNotificationsService) ya se
            // guardaban antes de existir la columna y quedaron con el valor por
            // defecto ("buyer"). Se reclasifican por su etiqueta para que no
            // aparezcan en el historial de clienta.
            migrationBuilder.Sql(
                "UPDATE \"Notifications\" SET \"Audience\" = 'seller' " +
                "WHERE \"Tag\" IN ('pedidos-por-vencer', 'saldos-sin-cobrar', 'pulso-negocio');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_AccountId_Audience",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "Audience",
                table: "Notifications");
        }
    }
}
