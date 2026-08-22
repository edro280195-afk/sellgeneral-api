using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFirebaseIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_IdentityMethod",
                table: "Accounts");

            migrationBuilder.AddColumn<string>(
                name: "FirebaseUid",
                table: "Accounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_FirebaseUid",
                table: "Accounts",
                column: "FirebaseUid",
                unique: true,
                filter: "\"FirebaseUid\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_IdentityMethod",
                table: "Accounts",
                sql: "\"Phone\" IS NOT NULL OR \"FirebaseUid\" IS NOT NULL OR \"FacebookUserId\" IS NOT NULL OR \"Email\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_FirebaseUid",
                table: "Accounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_IdentityMethod",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FirebaseUid",
                table: "Accounts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_IdentityMethod",
                table: "Accounts",
                sql: "\"Phone\" IS NOT NULL OR \"FacebookUserId\" IS NOT NULL OR \"Email\" IS NOT NULL");
        }
    }
}
