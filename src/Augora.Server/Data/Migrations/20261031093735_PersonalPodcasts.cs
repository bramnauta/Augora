using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF generates the key columns as an inline array

namespace Augora.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonalPodcasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Podcasts_FeedUrl",
                table: "Podcasts");

            migrationBuilder.AddColumn<string>(
                name: "LatestEpisodeKey",
                table: "Podcasts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LatestPublishedAt",
                table: "Podcasts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Podcasts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Subscriptions were shared by everyone: every existing user gets a copy of them.
            migrationBuilder.Sql(
                """
                INSERT INTO Podcasts (Id, UserId, FeedUrl, Title, Author, ImageUrl, SubscribedAt, LatestEpisodeKey, LatestPublishedAt)
                SELECT hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-8' || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)),
                       u.Id, p.FeedUrl, p.Title, p.Author, p.ImageUrl, p.SubscribedAt, NULL, NULL
                FROM Podcasts p CROSS JOIN AspNetUsers u
                WHERE p.UserId = '';
                """);
            migrationBuilder.Sql("DELETE FROM Podcasts WHERE UserId = '';");

            migrationBuilder.CreateTable(
                name: "PodcastProgress",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<string>(type: "TEXT", maxLength: 3000, nullable: false),
                    Position = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastProgress", x => new { x.UserId, x.EpisodeId });
                    table.ForeignKey(
                        name: "FK_PodcastProgress_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Podcasts_UserId_FeedUrl",
                table: "Podcasts",
                columns: new[] { "UserId", "FeedUrl" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Podcasts_AspNetUsers_UserId",
                table: "Podcasts",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Podcasts_AspNetUsers_UserId",
                table: "Podcasts");

            migrationBuilder.DropTable(
                name: "PodcastProgress");

            migrationBuilder.DropIndex(
                name: "IX_Podcasts_UserId_FeedUrl",
                table: "Podcasts");

            migrationBuilder.DropColumn(
                name: "LatestEpisodeKey",
                table: "Podcasts");

            migrationBuilder.DropColumn(
                name: "LatestPublishedAt",
                table: "Podcasts");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Podcasts");

            // Back to shared subscriptions: one row per feed.
            migrationBuilder.Sql("DELETE FROM Podcasts WHERE rowid NOT IN (SELECT MIN(rowid) FROM Podcasts GROUP BY FeedUrl);");

            migrationBuilder.CreateIndex(
                name: "IX_Podcasts_FeedUrl",
                table: "Podcasts",
                column: "FeedUrl",
                unique: true);
        }
    }
}
