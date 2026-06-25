using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    public partial class AddWinnerCountToRaffles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op intencional: winner_count ya fue creado en AddRaffleModuleEnhanced.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op intencional: la columna pertenece a AddRaffleModuleEnhanced.
        }
    }
}
