using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessOrderNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrderNumber",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

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

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessId_OrderNumber",
                table: "Orders",
                columns: new[] { "BusinessId", "OrderNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_BusinessId_OrderNumber",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OrderNumber",
                table: "Orders");
        }
    }
}
