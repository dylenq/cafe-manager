using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cafe.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentCommandLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentCommandLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Command = table.Column<int>(type: "INTEGER", nullable: false),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCommandLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCommandLogs_AtUtc",
                table: "AgentCommandLogs",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCommandLogs_StationId",
                table: "AgentCommandLogs",
                column: "StationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCommandLogs");
        }
    }
}
