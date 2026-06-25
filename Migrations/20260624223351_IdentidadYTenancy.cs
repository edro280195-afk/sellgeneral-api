using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class IdentidadYTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_SKU",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Clients_Name",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_ClientAliases_NormalizedAlias",
                table: "ClientAliases");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT (
                        EXISTS (SELECT 1 FROM "Users")
                        OR EXISTS (SELECT 1 FROM "Clients")
                        OR EXISTS (SELECT 1 FROM "Orders")
                        OR EXISTS (SELECT 1 FROM "DeliveryRoutes")
                        OR EXISTS (SELECT 1 FROM "Deliveries")
                        OR EXISTS (SELECT 1 FROM "Products")
                        OR EXISTS (SELECT 1 FROM "Suppliers")
                        OR EXISTS (SELECT 1 FROM "Investments")
                        OR EXISTS (SELECT 1 FROM "CashRegisterSessions")
                        OR EXISTS (SELECT 1 FROM "LoyaltyRewards")
                        OR EXISTS (SELECT 1 FROM "FcmTokens")
                        OR EXISTS (SELECT 1 FROM "PushSubscriptions")
                        OR EXISTS (SELECT 1 FROM "SalesPeriods")
                        OR EXISTS (SELECT 1 FROM "OrderPayments")
                        OR EXISTS (SELECT 1 FROM "OrderPackages")
                        OR EXISTS (SELECT 1 FROM products)
                        OR EXISTS (SELECT 1 FROM tandas)
                        OR EXISTS (SELECT 1 FROM raffle_entries)
                    ) THEN
                        DELETE FROM "AppSettings" WHERE "Id" = 1;
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<int>(
                name: "business_id",
                table: "tandas",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "business_id",
                table: "tanda_participants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Suppliers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "SalesPeriods",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "business_id",
                table: "raffles",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "business_id",
                table: "raffle_participants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "business_id",
                table: "raffle_entries",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "business_id",
                table: "raffle_draws",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "PushSubscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Products",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "business_id",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "business_id",
                table: "payments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "OrderPayments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "OrderPackages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "OrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "LoyaltyTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "LoyaltyRewards",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "LiveSpokenOrders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "LiveSessions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "LiveProducts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "LiveCommentOrders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "LiveCandidates",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Investments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "FcmTokens",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "DriverExpenses",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "DeliveryRoutes",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "DeliveryEvidences",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Clients",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Clients",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "ClientAliases",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "CashRegisterSessions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "AppSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DisplayName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    ProfilePhotoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FacebookUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.CheckConstraint("CK_Accounts_IdentityMethod", "\"Phone\" IS NOT NULL OR \"FacebookUserId\" IS NOT NULL OR \"Email\" IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "Businesses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    City = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    FrontendUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DepotLat = table.Column<double>(type: "double precision", nullable: false),
                    DepotLng = table.Column<double>(type: "double precision", nullable: false),
                    GeocodingRegion = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, defaultValue: "Nuevo Laredo, Tamaulipas, MX"),
                    GeminiBusinessName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    MercadoPagoAccessToken = table.Column<string>(type: "text", nullable: true),
                    PlanTier = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "Entrada"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Businesses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Memberships_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Memberships_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tandas_business_id",
                table: "tandas",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_tanda_participants_business_id",
                table: "tanda_participants",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BusinessId",
                table: "Suppliers",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesPeriods_BusinessId",
                table: "SalesPeriods",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_raffles_business_id",
                table: "raffles",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffle_participants_business_id",
                table: "raffle_participants",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffle_entries_business_id",
                table: "raffle_entries",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffle_draws_business_id",
                table: "raffle_draws",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_BusinessId",
                table: "PushSubscriptions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_BusinessId",
                table: "Products",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_BusinessId_SKU",
                table: "Products",
                columns: new[] { "BusinessId", "SKU" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_products_business_id",
                table: "products",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_business_id",
                table: "payments",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessId",
                table: "Orders",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderPayments_BusinessId",
                table: "OrderPayments",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderPackages_BusinessId",
                table: "OrderPackages",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_BusinessId",
                table: "OrderItems",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LoyaltyTransactions_BusinessId",
                table: "LoyaltyTransactions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LoyaltyRewards_BusinessId",
                table: "LoyaltyRewards",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSpokenOrders_BusinessId",
                table: "LiveSpokenOrders",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_BusinessId",
                table: "LiveSessions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveProducts_BusinessId",
                table: "LiveProducts",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCommentOrders_BusinessId",
                table: "LiveCommentOrders",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_BusinessId",
                table: "LiveCandidates",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Investments_BusinessId",
                table: "Investments",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_FcmTokens_BusinessId",
                table: "FcmTokens",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverExpenses_BusinessId",
                table: "DriverExpenses",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryRoutes_BusinessId",
                table: "DeliveryRoutes",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryEvidences_BusinessId",
                table: "DeliveryEvidences",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_BusinessId",
                table: "Deliveries",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_AccountId",
                table: "Clients",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_BusinessId",
                table: "Clients",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_BusinessId_Name",
                table: "Clients",
                columns: new[] { "BusinessId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientAliases_BusinessId",
                table: "ClientAliases",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientAliases_NormalizedAlias",
                table: "ClientAliases",
                columns: new[] { "BusinessId", "NormalizedAlias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashRegisterSessions_BusinessId",
                table: "CashRegisterSessions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_BusinessId",
                table: "AppSettings",
                column: "BusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Email",
                table: "Accounts",
                column: "Email",
                unique: true,
                filter: "\"Email\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_FacebookUserId",
                table: "Accounts",
                column: "FacebookUserId",
                unique: true,
                filter: "\"FacebookUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Phone",
                table: "Accounts",
                column: "Phone",
                unique: true,
                filter: "\"Phone\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Businesses_Slug",
                table: "Businesses",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_AccountId_BusinessId",
                table: "Memberships",
                columns: new[] { "AccountId", "BusinessId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_BusinessId",
                table: "Memberships",
                column: "BusinessId");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF (
                        EXISTS (SELECT 1 FROM "Users")
                        OR EXISTS (SELECT 1 FROM "Clients")
                        OR EXISTS (SELECT 1 FROM "Orders")
                        OR EXISTS (SELECT 1 FROM "DeliveryRoutes")
                        OR EXISTS (SELECT 1 FROM "Deliveries")
                        OR EXISTS (SELECT 1 FROM "Products")
                        OR EXISTS (SELECT 1 FROM "Suppliers")
                        OR EXISTS (SELECT 1 FROM "Investments")
                        OR EXISTS (SELECT 1 FROM "CashRegisterSessions")
                        OR EXISTS (SELECT 1 FROM "LoyaltyRewards")
                        OR EXISTS (SELECT 1 FROM "FcmTokens")
                        OR EXISTS (SELECT 1 FROM "PushSubscriptions")
                        OR EXISTS (SELECT 1 FROM "SalesPeriods")
                        OR EXISTS (SELECT 1 FROM "OrderPayments")
                        OR EXISTS (SELECT 1 FROM "OrderPackages")
                        OR EXISTS (SELECT 1 FROM products)
                        OR EXISTS (SELECT 1 FROM tandas)
                        OR EXISTS (SELECT 1 FROM raffle_entries)
                    ) THEN
                        INSERT INTO "Businesses"
                            ("Id", "Name", "Slug", "City", "FrontendUrl", "DepotLat", "DepotLng",
                             "GeocodingRegion", "GeminiBusinessName", "PlanTier", "IsActive", "CreatedAt")
                        VALUES
                            (1, 'Regi Bazar', 'regibazar', 'Nuevo Laredo', 'https://regibazar.com',
                             27.4861, -99.5069, 'Nuevo Laredo, Tamaulipas, MX',
                             'Regi Bazar', 'Elite', TRUE, NOW())
                        ON CONFLICT ("Id") DO NOTHING;

                        PERFORM setval(
                            pg_get_serial_sequence('"Businesses"', 'Id'),
                            GREATEST((SELECT COALESCE(MAX("Id"), 1) FROM "Businesses"), 1));
                    END IF;
                END $$;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_AppSettings_Businesses_BusinessId",
                table: "AppSettings",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CashRegisterSessions_Businesses_BusinessId",
                table: "CashRegisterSessions",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientAliases_Businesses_BusinessId",
                table: "ClientAliases",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_Accounts_AccountId",
                table: "Clients",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_Businesses_BusinessId",
                table: "Clients",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Deliveries_Businesses_BusinessId",
                table: "Deliveries",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DeliveryEvidences_Businesses_BusinessId",
                table: "DeliveryEvidences",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DeliveryRoutes_Businesses_BusinessId",
                table: "DeliveryRoutes",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DriverExpenses_Businesses_BusinessId",
                table: "DriverExpenses",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FcmTokens_Businesses_BusinessId",
                table: "FcmTokens",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Investments_Businesses_BusinessId",
                table: "Investments",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LiveCandidates_Businesses_BusinessId",
                table: "LiveCandidates",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LiveCommentOrders_Businesses_BusinessId",
                table: "LiveCommentOrders",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LiveProducts_Businesses_BusinessId",
                table: "LiveProducts",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LiveSessions_Businesses_BusinessId",
                table: "LiveSessions",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LiveSpokenOrders_Businesses_BusinessId",
                table: "LiveSpokenOrders",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LoyaltyRewards_Businesses_BusinessId",
                table: "LoyaltyRewards",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LoyaltyTransactions_Businesses_BusinessId",
                table: "LoyaltyTransactions",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_Businesses_BusinessId",
                table: "OrderItems",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderPackages_Businesses_BusinessId",
                table: "OrderPackages",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderPayments_Businesses_BusinessId",
                table: "OrderPayments",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Businesses_BusinessId",
                table: "Orders",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_Businesses_business_id",
                table: "payments",
                column: "business_id",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_Businesses_business_id",
                table: "products",
                column: "business_id",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Businesses_BusinessId",
                table: "Products",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PushSubscriptions_Businesses_BusinessId",
                table: "PushSubscriptions",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_raffle_draws_Businesses_business_id",
                table: "raffle_draws",
                column: "business_id",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_raffle_entries_Businesses_business_id",
                table: "raffle_entries",
                column: "business_id",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_raffle_participants_Businesses_business_id",
                table: "raffle_participants",
                column: "business_id",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_raffles_Businesses_business_id",
                table: "raffles",
                column: "business_id",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesPeriods_Businesses_BusinessId",
                table: "SalesPeriods",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Suppliers_Businesses_BusinessId",
                table: "Suppliers",
                column: "BusinessId",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_tanda_participants_Businesses_business_id",
                table: "tanda_participants",
                column: "business_id",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_tandas_Businesses_business_id",
                table: "tandas",
                column: "business_id",
                principalTable: "Businesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppSettings_Businesses_BusinessId",
                table: "AppSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_CashRegisterSessions_Businesses_BusinessId",
                table: "CashRegisterSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientAliases_Businesses_BusinessId",
                table: "ClientAliases");

            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Accounts_AccountId",
                table: "Clients");

            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Businesses_BusinessId",
                table: "Clients");

            migrationBuilder.DropForeignKey(
                name: "FK_Deliveries_Businesses_BusinessId",
                table: "Deliveries");

            migrationBuilder.DropForeignKey(
                name: "FK_DeliveryEvidences_Businesses_BusinessId",
                table: "DeliveryEvidences");

            migrationBuilder.DropForeignKey(
                name: "FK_DeliveryRoutes_Businesses_BusinessId",
                table: "DeliveryRoutes");

            migrationBuilder.DropForeignKey(
                name: "FK_DriverExpenses_Businesses_BusinessId",
                table: "DriverExpenses");

            migrationBuilder.DropForeignKey(
                name: "FK_FcmTokens_Businesses_BusinessId",
                table: "FcmTokens");

            migrationBuilder.DropForeignKey(
                name: "FK_Investments_Businesses_BusinessId",
                table: "Investments");

            migrationBuilder.DropForeignKey(
                name: "FK_LiveCandidates_Businesses_BusinessId",
                table: "LiveCandidates");

            migrationBuilder.DropForeignKey(
                name: "FK_LiveCommentOrders_Businesses_BusinessId",
                table: "LiveCommentOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_LiveProducts_Businesses_BusinessId",
                table: "LiveProducts");

            migrationBuilder.DropForeignKey(
                name: "FK_LiveSessions_Businesses_BusinessId",
                table: "LiveSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_LiveSpokenOrders_Businesses_BusinessId",
                table: "LiveSpokenOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_LoyaltyRewards_Businesses_BusinessId",
                table: "LoyaltyRewards");

            migrationBuilder.DropForeignKey(
                name: "FK_LoyaltyTransactions_Businesses_BusinessId",
                table: "LoyaltyTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_Businesses_BusinessId",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderPackages_Businesses_BusinessId",
                table: "OrderPackages");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderPayments_Businesses_BusinessId",
                table: "OrderPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Businesses_BusinessId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_Businesses_business_id",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_products_Businesses_business_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_Businesses_BusinessId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_PushSubscriptions_Businesses_BusinessId",
                table: "PushSubscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_raffle_draws_Businesses_business_id",
                table: "raffle_draws");

            migrationBuilder.DropForeignKey(
                name: "FK_raffle_entries_Businesses_business_id",
                table: "raffle_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_raffle_participants_Businesses_business_id",
                table: "raffle_participants");

            migrationBuilder.DropForeignKey(
                name: "FK_raffles_Businesses_business_id",
                table: "raffles");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesPeriods_Businesses_BusinessId",
                table: "SalesPeriods");

            migrationBuilder.DropForeignKey(
                name: "FK_Suppliers_Businesses_BusinessId",
                table: "Suppliers");

            migrationBuilder.DropForeignKey(
                name: "FK_tanda_participants_Businesses_business_id",
                table: "tanda_participants");

            migrationBuilder.DropForeignKey(
                name: "FK_tandas_Businesses_business_id",
                table: "tandas");

            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Businesses");

            migrationBuilder.DropIndex(
                name: "IX_tandas_business_id",
                table: "tandas");

            migrationBuilder.DropIndex(
                name: "IX_tanda_participants_business_id",
                table: "tanda_participants");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_BusinessId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_SalesPeriods_BusinessId",
                table: "SalesPeriods");

            migrationBuilder.DropIndex(
                name: "IX_raffles_business_id",
                table: "raffles");

            migrationBuilder.DropIndex(
                name: "IX_raffle_participants_business_id",
                table: "raffle_participants");

            migrationBuilder.DropIndex(
                name: "IX_raffle_entries_business_id",
                table: "raffle_entries");

            migrationBuilder.DropIndex(
                name: "IX_raffle_draws_business_id",
                table: "raffle_draws");

            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_BusinessId",
                table: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Products_BusinessId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_BusinessId_SKU",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_products_business_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_payments_business_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_Orders_BusinessId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_OrderPayments_BusinessId",
                table: "OrderPayments");

            migrationBuilder.DropIndex(
                name: "IX_OrderPackages_BusinessId",
                table: "OrderPackages");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_BusinessId",
                table: "OrderItems");

            migrationBuilder.DropIndex(
                name: "IX_LoyaltyTransactions_BusinessId",
                table: "LoyaltyTransactions");

            migrationBuilder.DropIndex(
                name: "IX_LoyaltyRewards_BusinessId",
                table: "LoyaltyRewards");

            migrationBuilder.DropIndex(
                name: "IX_LiveSpokenOrders_BusinessId",
                table: "LiveSpokenOrders");

            migrationBuilder.DropIndex(
                name: "IX_LiveSessions_BusinessId",
                table: "LiveSessions");

            migrationBuilder.DropIndex(
                name: "IX_LiveProducts_BusinessId",
                table: "LiveProducts");

            migrationBuilder.DropIndex(
                name: "IX_LiveCommentOrders_BusinessId",
                table: "LiveCommentOrders");

            migrationBuilder.DropIndex(
                name: "IX_LiveCandidates_BusinessId",
                table: "LiveCandidates");

            migrationBuilder.DropIndex(
                name: "IX_Investments_BusinessId",
                table: "Investments");

            migrationBuilder.DropIndex(
                name: "IX_FcmTokens_BusinessId",
                table: "FcmTokens");

            migrationBuilder.DropIndex(
                name: "IX_DriverExpenses_BusinessId",
                table: "DriverExpenses");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryRoutes_BusinessId",
                table: "DeliveryRoutes");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryEvidences_BusinessId",
                table: "DeliveryEvidences");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_BusinessId",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Clients_AccountId",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_BusinessId",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_BusinessId_Name",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_ClientAliases_BusinessId",
                table: "ClientAliases");

            migrationBuilder.DropIndex(
                name: "IX_ClientAliases_NormalizedAlias",
                table: "ClientAliases");

            migrationBuilder.DropIndex(
                name: "IX_CashRegisterSessions_BusinessId",
                table: "CashRegisterSessions");

            migrationBuilder.DropIndex(
                name: "IX_AppSettings_BusinessId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "tandas");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "tanda_participants");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "SalesPeriods");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "raffles");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "raffle_participants");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "raffle_entries");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "raffle_draws");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "OrderPayments");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "OrderPackages");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "LoyaltyTransactions");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "LoyaltyRewards");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "LiveSpokenOrders");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "LiveSessions");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "LiveProducts");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "LiveCommentOrders");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "LiveCandidates");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Investments");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "FcmTokens");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "DriverExpenses");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "DeliveryRoutes");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "DeliveryEvidences");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "ClientAliases");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "CashRegisterSessions");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "AppSettings");

            migrationBuilder.Sql("""
                INSERT INTO "AppSettings" ("Id", "DefaultShippingCost", "LinkExpirationHours")
                SELECT 1, 60, 72
                WHERE NOT EXISTS (SELECT 1 FROM "AppSettings" WHERE "Id" = 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Products_SKU",
                table: "Products",
                column: "SKU",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Name",
                table: "Clients",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientAliases_NormalizedAlias",
                table: "ClientAliases",
                column: "NormalizedAlias",
                unique: true);
        }
    }
}
