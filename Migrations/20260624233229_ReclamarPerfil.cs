using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class ReclamarPerfil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientClaimAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientClaimAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientClaimAudits_Account_Client",
                table: "ClientClaimAudits",
                columns: new[] { "AccountId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientClaimAudits_AccountId",
                table: "ClientClaimAudits",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientClaimAudits_ClientId",
                table: "ClientClaimAudits",
                column: "ClientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientClaimAudits");
        }
    }
}
