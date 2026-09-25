using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreFoundry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrationVersionAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The ProjectId foreign key needs an index starting with ProjectId at all times, so the
            // non-unique index is created (under a temporary name) before the unique one is dropped.
            migrationBuilder.CreateIndex(
                name: "IX_SchemaMigrations_ProjectId_Version_tmp",
                table: "SchemaMigrations",
                columns: new[] { "ProjectId", "Version" });

            migrationBuilder.DropIndex(
                name: "IX_SchemaMigrations_ProjectId_Version",
                table: "SchemaMigrations");

            migrationBuilder.RenameIndex(
                name: "IX_SchemaMigrations_ProjectId_Version_tmp",
                table: "SchemaMigrations",
                newName: "IX_SchemaMigrations_ProjectId_Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SchemaMigrations_ProjectId_Version_tmp",
                table: "SchemaMigrations",
                columns: new[] { "ProjectId", "Version" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_SchemaMigrations_ProjectId_Version",
                table: "SchemaMigrations");

            migrationBuilder.RenameIndex(
                name: "IX_SchemaMigrations_ProjectId_Version_tmp",
                table: "SchemaMigrations",
                newName: "IX_SchemaMigrations_ProjectId_Version");
        }
    }
}
