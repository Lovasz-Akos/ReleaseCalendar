using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseCalendar.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Apps",
                columns: table => new
                {
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    StoreUrlPath = table.Column<string>(type: "TEXT", nullable: true),
                    CapsuleUrl = table.Column<string>(type: "TEXT", nullable: true),
                    IsFree = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Precision = table.Column<int>(type: "INTEGER", nullable: false),
                    IsComingSoon = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEarlyAccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    CustomMessage = table.Column<string>(type: "TEXT", nullable: true),
                    EarlyAccessExitDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    FetchedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Apps", x => x.AppId);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WishlistItems = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnedItems = table.Column<int>(type: "INTEGER", nullable: false),
                    AppsRefreshed = table.Column<int>(type: "INTEGER", nullable: false),
                    DateChanges = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DateChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OldDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    OldPrecision = table.Column<int>(type: "INTEGER", nullable: false),
                    NewDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    NewPrecision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DateChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DateChanges_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "AppId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Entries",
                columns: table => new
                {
                    SteamId = table.Column<long>(type: "INTEGER", nullable: false),
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    AddedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PlaytimeMinutes = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => new { x.SteamId, x.AppId, x.Source });
                    table.ForeignKey(
                        name: "FK_Entries_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "AppId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Apps_FetchedUtc",
                table: "Apps",
                column: "FetchedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Apps_ReleaseDate",
                table: "Apps",
                column: "ReleaseDate");

            migrationBuilder.CreateIndex(
                name: "IX_DateChanges_AppId",
                table: "DateChanges",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_DateChanges_SeenUtc",
                table: "DateChanges",
                column: "SeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_AppId",
                table: "Entries",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_StartedUtc",
                table: "SyncRuns",
                column: "StartedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DateChanges");

            migrationBuilder.DropTable(
                name: "Entries");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropTable(
                name: "Apps");
        }
    }
}
