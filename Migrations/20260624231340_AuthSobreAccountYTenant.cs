using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AuthSobreAccountYTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashRegisterSessions_Users_UserId",
                table: "CashRegisterSessions");

            migrationBuilder.Sql("""
                UPDATE "Accounts"
                SET "Email" = lower("Email")
                WHERE "Email" IS NOT NULL;

                INSERT INTO "Accounts" ("DisplayName", "Email", "PasswordHash", "CreatedAt")
                SELECT
                    COALESCE(NULLIF(u."Name", ''), u."Email"),
                    lower(u."Email"),
                    u."PasswordHash",
                    u."CreatedAt"
                FROM "Users" u
                WHERE u."Email" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "Accounts" a
                      WHERE a."Email" = lower(u."Email"));

                INSERT INTO "Memberships" ("AccountId", "BusinessId", "Role", "CreatedAt")
                SELECT
                    a."Id",
                    1,
                    CASE lower(COALESCE(u."Rol", ''))
                        WHEN 'admin' THEN 0
                        WHEN 'driver' THEN 2
                        WHEN 'scaner' THEN 3
                        ELSE 1
                    END,
                    NOW()
                FROM "Users" u
                JOIN "Accounts" a ON a."Email" = lower(u."Email")
                WHERE EXISTS (SELECT 1 FROM "Businesses" b WHERE b."Id" = 1)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "Memberships" m
                      WHERE m."AccountId" = a."Id"
                        AND m."BusinessId" = 1);
                """);

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "CashRegisterSessions",
                newName: "AccountId");

            migrationBuilder.RenameIndex(
                name: "IX_CashRegisterSessions_UserId",
                table: "CashRegisterSessions",
                newName: "IX_CashRegisterSessions_AccountId");

            migrationBuilder.Sql("""
                UPDATE "CashRegisterSessions" s
                SET "AccountId" = a."Id"
                FROM "Users" u
                JOIN "Accounts" a ON a."Email" = lower(u."Email")
                WHERE s."AccountId" = u."Id";

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "CashRegisterSessions" s
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM "Accounts" a
                            WHERE a."Id" = s."AccountId"))
                    THEN
                        RAISE EXCEPTION 'CashRegisterSessions contiene AccountId sin Account migrada.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_CashRegisterSessions_Accounts_AccountId",
                table: "CashRegisterSessions",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropTable(
                name: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashRegisterSessions_Accounts_AccountId",
                table: "CashRegisterSessions");

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Rol = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "Users" ("Id", "Name", "Email", "PasswordHash", "CreatedAt", "Rol")
                SELECT
                    a."Id",
                    a."DisplayName",
                    COALESCE(a."Email", 'account-' || a."Id" || '@example.local'),
                    COALESCE(a."PasswordHash", ''),
                    a."CreatedAt",
                    CASE COALESCE(m."Role", 1)
                        WHEN 0 THEN 'Admin'
                        WHEN 2 THEN 'Driver'
                        WHEN 3 THEN 'Scaner'
                        ELSE 'Admin'
                    END
                FROM "Accounts" a
                LEFT JOIN "Memberships" m
                    ON m."AccountId" = a."Id"
                   AND m."BusinessId" = 1
                WHERE a."PasswordHash" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "Users" u
                      WHERE u."Id" = a."Id");

                SELECT setval(
                    pg_get_serial_sequence('"Users"', 'Id'),
                    GREATEST((SELECT COALESCE(MAX("Id"), 1) FROM "Users"), 1));
                """);

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "CashRegisterSessions",
                newName: "UserId");

            migrationBuilder.RenameIndex(
                name: "IX_CashRegisterSessions_AccountId",
                table: "CashRegisterSessions",
                newName: "IX_CashRegisterSessions_UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashRegisterSessions_Users_UserId",
                table: "CashRegisterSessions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
