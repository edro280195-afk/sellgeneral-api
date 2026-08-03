using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderMergeSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MergedAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MergedIntoOrderId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalClientId",
                table: "OrderItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalClientName",
                table: "OrderItems",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalOrderId",
                table: "OrderItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrderMergeAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SourceOrderId = table.Column<int>(type: "integer", nullable: false),
                    SourceClientId = table.Column<int>(type: "integer", nullable: false),
                    SourceClientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetOrderId = table.Column<int>(type: "integer", nullable: false),
                    TargetClientId = table.Column<int>(type: "integer", nullable: false),
                    TargetClientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ItemsMoved = table.Column<int>(type: "integer", nullable: false),
                    AmountMoved = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    PaymentsMoved = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    MergedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderMergeAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderMergeAudits_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_MergedIntoOrderId",
                table: "Orders",
                column: "MergedIntoOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderMergeAudits_BusinessId",
                table: "OrderMergeAudits",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderMergeAudits_MergedAt",
                table: "OrderMergeAudits",
                column: "MergedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OrderMergeAudits_SourceOrderId",
                table: "OrderMergeAudits",
                column: "SourceOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderMergeAudits_TargetOrderId",
                table: "OrderMergeAudits",
                column: "TargetOrderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Orders_MergedIntoOrderId",
                table: "Orders",
                column: "MergedIntoOrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Orders_MergedIntoOrderId",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "OrderMergeAudits");

            migrationBuilder.DropIndex(
                name: "IX_Orders_MergedIntoOrderId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "MergedIntoOrderId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OriginalClientId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "OriginalClientName",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "OriginalOrderId",
                table: "OrderItems");
        }
    }
}
