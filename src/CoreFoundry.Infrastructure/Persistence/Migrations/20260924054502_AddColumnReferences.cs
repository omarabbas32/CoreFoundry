using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreFoundry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "OnDelete",
                table: "ProjectColumns",
                type: "tinyint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReferencesTableId",
                table: "ProjectColumns",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectColumns_ReferencesTableId",
                table: "ProjectColumns",
                column: "ReferencesTableId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectColumns_ProjectTables_ReferencesTableId",
                table: "ProjectColumns",
                column: "ReferencesTableId",
                principalTable: "ProjectTables",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectColumns_ProjectTables_ReferencesTableId",
                table: "ProjectColumns");

            migrationBuilder.DropIndex(
                name: "IX_ProjectColumns_ReferencesTableId",
                table: "ProjectColumns");

            migrationBuilder.DropColumn(
                name: "OnDelete",
                table: "ProjectColumns");

            migrationBuilder.DropColumn(
                name: "ReferencesTableId",
                table: "ProjectColumns");
        }
    }
}
