using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseCalendar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddResolvedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Resolved",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Resolved",
                table: "Apps");
        }
    }
}
