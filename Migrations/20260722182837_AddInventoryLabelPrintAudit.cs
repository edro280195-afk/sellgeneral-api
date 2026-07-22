using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryLabelPrintAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryLabelPrints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    LabelTemplateVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaSize = table.Column<int>(type: "integer", nullable: false),
                    Output = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Copies = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HandedOffAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryLabelPrints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryLabelPrints_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryLabelPrints_LabelTemplateVersions_LabelTemplateVer~",
                        column: x => x.LabelTemplateVersionId,
                        principalTable: "LabelTemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLabelPrints_BusinessId",
                table: "InventoryLabelPrints",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLabelPrints_BusinessId_Kind_TargetId",
                table: "InventoryLabelPrints",
                columns: new[] { "BusinessId", "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLabelPrints_BusinessId_RequestedAt",
                table: "InventoryLabelPrints",
                columns: new[] { "BusinessId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLabelPrints_LabelTemplateVersionId",
                table: "InventoryLabelPrints",
                column: "LabelTemplateVersionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryLabelPrints");
        }
    }
}
