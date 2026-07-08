using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreFollowers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ClientId",
                table: "Notifications",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Notifications",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BuyerDeviceTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuyerDeviceTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoreFollowers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    NotifyOnPost = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyOnLive = table.Column<bool>(type: "boolean", nullable: false),
                    IsVip = table.Column<bool>(type: "boolean", nullable: false),
                    VipSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UnfollowedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreFollowers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoreFollowers_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuyerDeviceTokens_AccountId",
                table: "BuyerDeviceTokens",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BuyerDeviceTokens_Token",
                table: "BuyerDeviceTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreFollowers_AccountId",
                table: "StoreFollowers",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreFollowers_BusinessId",
                table: "StoreFollowers",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreFollowers_BusinessId_AccountId",
                table: "StoreFollowers",
                columns: new[] { "BusinessId", "AccountId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuyerDeviceTokens");

            migrationBuilder.DropTable(
                name: "StoreFollowers");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Notifications");

            migrationBuilder.AlterColumn<int>(
                name: "ClientId",
                table: "Notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
