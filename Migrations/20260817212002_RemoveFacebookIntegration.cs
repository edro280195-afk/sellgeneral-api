using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class RemoveFacebookIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_FacebookUserId",
                table: "Accounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_IdentityMethod",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FacebookProfileUrl",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "FacebookUrl",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "MessengerUrl",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "FacebookUserId",
                table: "Accounts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_IdentityMethod",
                table: "Accounts",
                sql: "\"Phone\" IS NOT NULL OR \"FirebaseUid\" IS NOT NULL OR \"Email\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_IdentityMethod",
                table: "Accounts");

            migrationBuilder.AddColumn<string>(
                name: "FacebookProfileUrl",
                table: "Clients",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FacebookUrl",
                table: "Businesses",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessengerUrl",
                table: "Businesses",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FacebookUserId",
                table: "Accounts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_FacebookUserId",
                table: "Accounts",
                column: "FacebookUserId",
                unique: true,
                filter: "\"FacebookUserId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_IdentityMethod",
                table: "Accounts",
                sql: "\"Phone\" IS NOT NULL OR \"FirebaseUid\" IS NOT NULL OR \"FacebookUserId\" IS NOT NULL OR \"Email\" IS NOT NULL");
        }
    }
}
