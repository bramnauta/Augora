using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioCentral.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpotifyEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "SpotifyAccounts",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Librespot");

            migrationBuilder.AddColumn<string>(
                name: "ProtectedSoloistApiKey",
                table: "SpotifyAccounts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Engine",
                table: "SpotifyAccounts");

            migrationBuilder.DropColumn(
                name: "ProtectedSoloistApiKey",
                table: "SpotifyAccounts");
        }
    }
}
