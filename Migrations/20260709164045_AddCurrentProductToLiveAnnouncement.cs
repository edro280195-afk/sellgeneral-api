using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrentProductToLiveAnnouncement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentAnnouncedAt",
                table: "LiveAnnouncements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentProductId",
                table: "LiveAnnouncements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentProductName",
                table: "LiveAnnouncements",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CurrentProductPrice",
                table: "LiveAnnouncements",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentAnnouncedAt",
                table: "LiveAnnouncements");

            migrationBuilder.DropColumn(
                name: "CurrentProductId",
                table: "LiveAnnouncements");

            migrationBuilder.DropColumn(
                name: "CurrentProductName",
                table: "LiveAnnouncements");

            migrationBuilder.DropColumn(
                name: "CurrentProductPrice",
                table: "LiveAnnouncements");
        }
    }
}
