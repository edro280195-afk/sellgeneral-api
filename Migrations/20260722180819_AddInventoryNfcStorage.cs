using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryNfcStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryBoxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NfcToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NfcTagUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsNfcBound = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryBoxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryBoxes_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCountSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    InventoryBoxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PerformedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CountedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCountSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCountSessions_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryCountSessions_InventoryBoxes_InventoryBoxId",
                        column: x => x.InventoryBoxId,
                        principalTable: "InventoryBoxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    InventoryBoxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Variant = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Barcode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LabelCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryItems_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryItems_InventoryBoxes_InventoryBoxId",
                        column: x => x.InventoryBoxId,
                        principalTable: "InventoryBoxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCountEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    InventoryCountSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Variant = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ExpectedQuantity = table.Column<int>(type: "integer", nullable: false),
                    ActualQuantity = table.Column<int>(type: "integer", nullable: false),
                    Difference = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCountEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCountEntries_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryCountEntries_InventoryCountSessions_InventoryCount~",
                        column: x => x.InventoryCountSessionId,
                        principalTable: "InventoryCountSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    InventoryBoxId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransferGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    QuantityDelta = table.Column<int>(type: "integer", nullable: false),
                    QuantityAfter = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PerformedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_InventoryBoxes_InventoryBoxId",
                        column: x => x.InventoryBoxId,
                        principalTable: "InventoryBoxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBoxes_BusinessId",
                table: "InventoryBoxes",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBoxes_BusinessId_Code",
                table: "InventoryBoxes",
                columns: new[] { "BusinessId", "Code" },
                unique: true,
                filter: "\"IsArchived\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBoxes_NfcTagUid",
                table: "InventoryBoxes",
                column: "NfcTagUid",
                unique: true,
                filter: "\"NfcTagUid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBoxes_NfcToken",
                table: "InventoryBoxes",
                column: "NfcToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountEntries_BusinessId",
                table: "InventoryCountEntries",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountEntries_InventoryCountSessionId_InventoryItem~",
                table: "InventoryCountEntries",
                columns: new[] { "InventoryCountSessionId", "InventoryItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountSessions_BusinessId",
                table: "InventoryCountSessions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountSessions_InventoryBoxId_CountedAt",
                table: "InventoryCountSessions",
                columns: new[] { "InventoryBoxId", "CountedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_BusinessId",
                table: "InventoryItems",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_BusinessId_LabelCode",
                table: "InventoryItems",
                columns: new[] { "BusinessId", "LabelCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_InventoryBoxId",
                table: "InventoryItems",
                column: "InventoryBoxId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_BusinessId",
                table: "InventoryMovements",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_InventoryBoxId_OccurredAt",
                table: "InventoryMovements",
                columns: new[] { "InventoryBoxId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_InventoryItemId",
                table: "InventoryMovements",
                column: "InventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_TransferGroupId",
                table: "InventoryMovements",
                column: "TransferGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryCountEntries");

            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "InventoryCountSessions");

            migrationBuilder.DropTable(
                name: "InventoryItems");

            migrationBuilder.DropTable(
                name: "InventoryBoxes");
        }
    }
}
