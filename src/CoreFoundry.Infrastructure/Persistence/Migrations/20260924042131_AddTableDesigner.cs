using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreFoundry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTableDesigner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ProjectTables",
                type: "int",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "ProjectTables");
        }
    }
}
