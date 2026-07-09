using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class DropLiveCapturePipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveCandidates");

            migrationBuilder.DropTable(
                name: "LiveCommentOrders");

            migrationBuilder.DropTable(
                name: "LiveSpokenOrders");

            migrationBuilder.DropTable(
                name: "LiveProducts");

            migrationBuilder.DropTable(
                name: "LiveSessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    FacebookUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LocalAudioPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    R2Key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StatusDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Transcript = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveSessions_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LiveCommentOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CommentDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CommentedAtSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OcrConfidence = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveCommentOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveCommentOrders_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LiveCommentOrders_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    AnnouncedAtSeconds = table.Column<double>(type: "double precision", nullable: true),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveProducts_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LiveProducts_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveSpokenOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ClientNameSpoken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SpokenAtSeconds = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSpokenOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveSpokenOrders_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LiveSpokenOrders_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveCandidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveProductId = table.Column<int>(type: "integer", nullable: true),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    ResolvedClientId = table.Column<int>(type: "integer", nullable: true),
                    BusinessId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ClientNameSpoken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CommentDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedOrderId = table.Column<int>(type: "integer", nullable: true),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProposedAliasPairJson = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SpokenAtSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveCandidates_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LiveCandidates_Clients_ResolvedClientId",
                        column: x => x.ResolvedClientId,
                        principalTable: "Clients",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LiveCandidates_LiveProducts_LiveProductId",
                        column: x => x.LiveProductId,
                        principalTable: "LiveProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LiveCandidates_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_BusinessId",
                table: "LiveCandidates",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_LiveProductId",
                table: "LiveCandidates",
                column: "LiveProductId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_LiveSessionId",
                table: "LiveCandidates",
                column: "LiveSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_ResolvedClientId",
                table: "LiveCandidates",
                column: "ResolvedClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCommentOrders_BusinessId",
                table: "LiveCommentOrders",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCommentOrders_LiveSessionId",
                table: "LiveCommentOrders",
                column: "LiveSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveProducts_BusinessId",
                table: "LiveProducts",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveProducts_LiveSessionId",
                table: "LiveProducts",
                column: "LiveSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_BusinessId",
                table: "LiveSessions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSpokenOrders_BusinessId",
                table: "LiveSpokenOrders",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSpokenOrders_LiveSessionId",
                table: "LiveSpokenOrders",
                column: "LiveSessionId");
        }
    }
}
