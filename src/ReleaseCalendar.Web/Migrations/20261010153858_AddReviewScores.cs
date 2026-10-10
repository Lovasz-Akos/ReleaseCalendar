using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseCalendar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReviewCount",
                table: "Apps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewLabel",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewPercentPositive",
                table: "Apps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewScore",
                table: "Apps",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewCount",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ReviewLabel",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ReviewPercentPositive",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ReviewScore",
                table: "Apps");
        }
    }
}
