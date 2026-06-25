using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class EntitlementsFase10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentPeriodEndsAt",
                table: "Businesses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionStatus",
                table: "Businesses",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialEndsAt",
                table: "Businesses",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentPeriodEndsAt",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "SubscriptionStatus",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "TrialEndsAt",
                table: "Businesses");
        }
    }
}
