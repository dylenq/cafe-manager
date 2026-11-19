using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cafe.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueAgentMachineId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AgentMachineId",
                table: "Stations",
                type: "TEXT",
                maxLength: 100,
                nullable: true,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stations_AgentMachineId",
                table: "Stations",
                column: "AgentMachineId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Stations_AgentMachineId",
                table: "Stations");

            migrationBuilder.AlterColumn<string>(
                name: "AgentMachineId",
                table: "Stations",
                type: "TEXT",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100,
                oldNullable: true,
                oldCollation: "NOCASE");
        }
    }
}
