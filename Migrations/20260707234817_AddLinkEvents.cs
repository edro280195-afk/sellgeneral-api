using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OrderAccessToken = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Event = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Referrer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkEvents_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkEvents_BusinessId",
                table: "LinkEvents",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkEvents_CreatedAt",
                table: "LinkEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LinkEvents_Event",
                table: "LinkEvents",
                column: "Event");

            migrationBuilder.CreateIndex(
                name: "IX_LinkEvents_OrderAccessToken",
                table: "LinkEvents",
                column: "OrderAccessToken");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkEvents");
        }
    }
}
