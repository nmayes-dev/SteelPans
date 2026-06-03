using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteelPans.Shared.Migrations
{
    /// <inheritdoc />
    public partial class GroupManagementRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AdminSince",
                table: "GroupMembers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupInvites_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvites_GroupId",
                table: "GroupInvites",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvites_Token",
                table: "GroupInvites",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupInvites");

            migrationBuilder.DropColumn(
                name: "AdminSince",
                table: "GroupMembers");
        }
    }
}
