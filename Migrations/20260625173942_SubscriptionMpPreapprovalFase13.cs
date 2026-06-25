using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionMpPreapprovalFase13 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CancellationEffectiveAt",
                table: "Businesses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerEmail",
                table: "Businesses",
                type: "character varying(180)",
                maxLength: 180,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreapprovalId",
                table: "Businesses",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreapprovalStatus",
                table: "Businesses",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionPeriodMonths",
                table: "Businesses",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationEffectiveAt",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "PayerEmail",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "PreapprovalId",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "PreapprovalStatus",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "SubscriptionPeriodMonths",
                table: "Businesses");
        }
    }
}
