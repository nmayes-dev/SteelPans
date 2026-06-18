using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SteelPans.Shared.Data;

#nullable disable

namespace SteelPans.Shared.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(EnsembleDbContext))]
    [Migration("20260621010940_AddTrackIdToMidiTrackAssignments")]
    public partial class AddTrackIdToMidiTrackAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MidiTrackAssignments_MidiFileId_TrackIndex",
                table: "MidiTrackAssignments");

            migrationBuilder.AddColumn<Guid>(
                name: "TrackId",
                table: "MidiTrackAssignments",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.Sql("""
                UPDATE "MidiTrackAssignments" AS a
                SET "TrackId" = t."Id"
                FROM "MidiTracks" AS t
                WHERE a."MidiFileId" = t."MidiFileId"
                  AND a."TrackIndex" = t."TrackIndex";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MidiTrackAssignments_MidiFileId_TrackIndex",
                table: "MidiTrackAssignments",
                columns: new[] { "MidiFileId", "TrackIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_MidiTrackAssignments_MidiFileId_TrackId",
                table: "MidiTrackAssignments",
                columns: new[] { "MidiFileId", "TrackId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MidiTrackAssignments_TrackId",
                table: "MidiTrackAssignments",
                column: "TrackId");

            migrationBuilder.AddForeignKey(
                name: "FK_MidiTrackAssignments_MidiTracks_TrackId",
                table: "MidiTrackAssignments",
                column: "TrackId",
                principalTable: "MidiTracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MidiTrackAssignments_MidiTracks_TrackId",
                table: "MidiTrackAssignments");

            migrationBuilder.DropIndex(
                name: "IX_MidiTrackAssignments_MidiFileId_TrackIndex",
                table: "MidiTrackAssignments");

            migrationBuilder.DropIndex(
                name: "IX_MidiTrackAssignments_MidiFileId_TrackId",
                table: "MidiTrackAssignments");

            migrationBuilder.DropIndex(
                name: "IX_MidiTrackAssignments_TrackId",
                table: "MidiTrackAssignments");

            migrationBuilder.DropColumn(
                name: "TrackId",
                table: "MidiTrackAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_MidiTrackAssignments_MidiFileId_TrackIndex",
                table: "MidiTrackAssignments",
                columns: new[] { "MidiFileId", "TrackIndex" },
                unique: true);
        }
    }
}
