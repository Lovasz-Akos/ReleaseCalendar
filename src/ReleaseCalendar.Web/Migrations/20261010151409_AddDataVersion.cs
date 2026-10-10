using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseCalendar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDataVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DataVersion",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataVersion",
                table: "Apps");
        }
    }
}
