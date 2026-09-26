using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreFoundry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TableAccessRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "ReadAccess",
                table: "ProjectTables",
                type: "tinyint unsigned",
                nullable: false,
                defaultValue: (byte)2);

            migrationBuilder.AddColumn<byte>(
                name: "WriteAccess",
                table: "ProjectTables",
                type: "tinyint unsigned",
                nullable: false,
                defaultValue: (byte)2);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadAccess",
                table: "ProjectTables");

            migrationBuilder.DropColumn(
                name: "WriteAccess",
                table: "ProjectTables");
        }
    }
}
