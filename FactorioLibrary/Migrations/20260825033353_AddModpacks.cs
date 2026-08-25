using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactorioLibrary.Migrations
{
    /// <inheritdoc />
    public partial class AddModpacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Modpacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TargetFactorioVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ModListJson = table.Column<string>(type: "TEXT", nullable: false),
                    ModSettingsDat = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Modpacks", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Modpacks");
        }
    }
}
