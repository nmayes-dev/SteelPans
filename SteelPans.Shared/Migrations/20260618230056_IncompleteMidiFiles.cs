using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteelPans.Shared.Migrations
{
    /// <inheritdoc />
    public partial class IncompleteMidiFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsIncomplete",
                table: "MidiFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsIncomplete",
                table: "MidiFiles");
        }
    }
}
