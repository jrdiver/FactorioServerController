using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactorioLibrary.Migrations
{
    /// <inheritdoc />
    public partial class AddIgnoredMods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IgnoredMods",
                table: "ServerInstances",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IgnoredMods",
                table: "ServerInstances");
        }
    }
}
