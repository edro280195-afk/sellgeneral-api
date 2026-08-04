using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingAndTrialProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BuyerOnboardingCompletedAtUtc",
                table: "Accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SellerOnboardingCompletedAtUtc",
                table: "Accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerTrialDeviceHash",
                table: "Accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SellerTrialEvaluatedAtUtc",
                table: "Accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SellerTrialGrantedAtUtc",
                table: "Accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerTrialRestrictionReason",
                table: "Accounts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Las cuentas que ya son Owner consumieron su prueba bajo el
            // modelo anterior. El backfill evita que reciban otra al crear un
            // negocio despues de desplegar esta proteccion.
            migrationBuilder.Sql("""
                UPDATE "Accounts" AS account
                SET
                    "SellerTrialGrantedAtUtc" = existing."FirstBusinessAt",
                    "SellerTrialEvaluatedAtUtc" = existing."FirstBusinessAt"
                FROM (
                    SELECT
                        membership."AccountId",
                        MIN(business."CreatedAt") AS "FirstBusinessAt"
                    FROM "Memberships" AS membership
                    INNER JOIN "Businesses" AS business
                        ON business."Id" = membership."BusinessId"
                    WHERE membership."Role" = 0
                    GROUP BY membership."AccountId"
                ) AS existing
                WHERE account."Id" = existing."AccountId"
                  AND account."SellerTrialGrantedAtUtc" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_SellerTrialDeviceHash",
                table: "Accounts",
                column: "SellerTrialDeviceHash",
                unique: true,
                filter: "\"SellerTrialDeviceHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_SellerTrialDeviceHash",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "BuyerOnboardingCompletedAtUtc",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SellerOnboardingCompletedAtUtc",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SellerTrialDeviceHash",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SellerTrialEvaluatedAtUtc",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SellerTrialGrantedAtUtc",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SellerTrialRestrictionReason",
                table: "Accounts");
        }
    }
}
