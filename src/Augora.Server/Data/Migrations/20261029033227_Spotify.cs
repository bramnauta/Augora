using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Augora.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Spotify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpotifyAccounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: true),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ProtectedRefreshToken = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotifyAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpotifyAccounts_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpotifyAccounts_OwnerUserId",
                table: "SpotifyAccounts",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpotifyAccounts");
        }
    }
}
