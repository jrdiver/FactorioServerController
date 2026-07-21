using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactorioLibrary.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessLevelToUserServerAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccessLevel",
                table: "UserServerAccesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessLevel",
                table: "UserServerAccesses");
        }
    }
}
