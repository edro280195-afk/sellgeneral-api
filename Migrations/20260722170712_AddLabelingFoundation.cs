using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLabelingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LabelAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Url = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelAssets_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LabelPrintJobItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    LabelPrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderPackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    PackageQrCodeValue = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelPrintJobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelPrintJobItems_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LabelPrintJobItems_OrderPackages_OrderPackageId",
                        column: x => x.OrderPackageId,
                        principalTable: "OrderPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LabelPrintJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    LabelTemplateVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaSize = table.Column<int>(type: "integer", nullable: false),
                    Output = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Copies = table.Column<int>(type: "integer", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HandedOffAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelPrintJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelPrintJobs_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LabelTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    MediaSize = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelTemplates_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LabelTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    LabelTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DesignJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PublishedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelTemplateVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelTemplateVersions_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LabelTemplateVersions_LabelTemplates_LabelTemplateId",
                        column: x => x.LabelTemplateId,
                        principalTable: "LabelTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LabelAssets_BusinessId",
                table: "LabelAssets",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelAssets_BusinessId_Name",
                table: "LabelAssets",
                columns: new[] { "BusinessId", "Name" },
                unique: true,
                filter: "\"IsArchived\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintJobItems_BusinessId",
                table: "LabelPrintJobItems",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintJobItems_BusinessId_OrderPackageId",
                table: "LabelPrintJobItems",
                columns: new[] { "BusinessId", "OrderPackageId" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintJobItems_LabelPrintJobId_Sequence",
                table: "LabelPrintJobItems",
                columns: new[] { "LabelPrintJobId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintJobItems_OrderPackageId",
                table: "LabelPrintJobItems",
                column: "OrderPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintJobs_BusinessId",
                table: "LabelPrintJobs",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintJobs_BusinessId_RequestedAt",
                table: "LabelPrintJobs",
                columns: new[] { "BusinessId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintJobs_LabelTemplateVersionId",
                table: "LabelPrintJobs",
                column: "LabelTemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_BusinessId",
                table: "LabelTemplates",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_BusinessId_Kind_MediaSize_IsDefault",
                table: "LabelTemplates",
                columns: new[] { "BusinessId", "Kind", "MediaSize", "IsDefault" },
                unique: true,
                filter: "\"IsDefault\" = TRUE AND \"IsArchived\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_BusinessId_Name",
                table: "LabelTemplates",
                columns: new[] { "BusinessId", "Name" },
                unique: true,
                filter: "\"IsArchived\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_PublishedVersionId",
                table: "LabelTemplates",
                column: "PublishedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplateVersions_BusinessId",
                table: "LabelTemplateVersions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplateVersions_LabelTemplateId_VersionNumber",
                table: "LabelTemplateVersions",
                columns: new[] { "LabelTemplateId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LabelPrintJobItems_LabelPrintJobs_LabelPrintJobId",
                table: "LabelPrintJobItems",
                column: "LabelPrintJobId",
                principalTable: "LabelPrintJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_LabelPrintJobs_LabelTemplateVersions_LabelTemplateVersionId",
                table: "LabelPrintJobs",
                column: "LabelTemplateVersionId",
                principalTable: "LabelTemplateVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LabelTemplates_LabelTemplateVersions_PublishedVersionId",
                table: "LabelTemplates",
                column: "PublishedVersionId",
                principalTable: "LabelTemplateVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LabelTemplates_LabelTemplateVersions_PublishedVersionId",
                table: "LabelTemplates");

            migrationBuilder.DropTable(
                name: "LabelAssets");

            migrationBuilder.DropTable(
                name: "LabelPrintJobItems");

            migrationBuilder.DropTable(
                name: "LabelPrintJobs");

            migrationBuilder.DropTable(
                name: "LabelTemplateVersions");

            migrationBuilder.DropTable(
                name: "LabelTemplates");
        }
    }
}
