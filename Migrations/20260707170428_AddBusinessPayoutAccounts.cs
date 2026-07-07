using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessPayoutAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PayoutAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    HolderName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BankName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Alias = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    AccountNumber = table.Column<string>(type: "character varying(700)", maxLength: 700, nullable: false),
                    MaskedNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NumberLength = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayoutAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayoutAccounts_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayoutAccounts_Business_Default",
                table: "PayoutAccounts",
                columns: new[] { "BusinessId", "IsActive", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_PayoutAccounts_BusinessId",
                table: "PayoutAccounts",
                column: "BusinessId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayoutAccounts");
        }
    }
}
