using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseCalendar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewSales",
                table: "SyncRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PriceChanges",
                table: "SyncRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DiscountEndsUtc",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscountPct",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FormattedOriginalPrice",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormattedPrice",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalPriceCents",
                table: "Apps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceCents",
                table: "Apps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SaleSeenUtc",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PricePoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinalCents = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalCents = table.Column<int>(type: "INTEGER", nullable: false),
                    DiscountPct = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricePoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PricePoints_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "AppId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PricePoints_AppId_SeenUtc",
                table: "PricePoints",
                columns: new[] { "AppId", "SeenUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PricePoints");

            migrationBuilder.DropColumn(
                name: "NewSales",
                table: "SyncRuns");

            migrationBuilder.DropColumn(
                name: "PriceChanges",
                table: "SyncRuns");

            migrationBuilder.DropColumn(
                name: "DiscountEndsUtc",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "DiscountPct",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "FormattedOriginalPrice",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "FormattedPrice",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "OriginalPriceCents",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PriceCents",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "SaleSeenUtc",
                table: "Apps");
        }
    }
}
