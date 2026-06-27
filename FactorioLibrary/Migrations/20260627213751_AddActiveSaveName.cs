using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactorioLibrary.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveSaveName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveSaveName",
                table: "ServerInstances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveSaveName",
                table: "ServerInstances");
        }
    }
}
