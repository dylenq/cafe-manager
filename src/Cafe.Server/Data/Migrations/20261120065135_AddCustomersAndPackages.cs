using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cafe.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomersAndPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MemberHourlyRate",
                table: "StationTypes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustomerId",
                table: "Sessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMemberPricing",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AppliesTo",
                table: "RateRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    IsMember = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimePackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    Minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Price = table.Column<string>(type: "TEXT", nullable: false),
                    StationTypeId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimePackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimePackages_StationTypes_StationTypeId",
                        column: x => x.StationTypeId,
                        principalTable: "StationTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PackageSales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    TimePackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Price = table.Column<string>(type: "TEXT", nullable: false),
                    Minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    StationTypeId = table.Column<int>(type: "INTEGER", nullable: true),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageSales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageSales_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PackageSales_TimePackages_TimePackageId",
                        column: x => x.TimePackageId,
                        principalTable: "TimePackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerMinuteLedger",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationTypeId = table.Column<int>(type: "INTEGER", nullable: true),
                    MinutesDelta = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    PackageSaleId = table.Column<int>(type: "INTEGER", nullable: true),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerMinuteLedger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerMinuteLedger_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerMinuteLedger_PackageSales_PackageSaleId",
                        column: x => x.PackageSaleId,
                        principalTable: "PackageSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerMinuteLedger_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerMinuteLedger_StationTypes_StationTypeId",
                        column: x => x.StationTypeId,
                        principalTable: "StationTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Amount = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PackageSaleId = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_PackageSales_PackageSaleId",
                        column: x => x.PackageSaleId,
                        principalTable: "PackageSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CustomerId",
                table: "Sessions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMinuteLedger_CustomerId_StationTypeId",
                table: "CustomerMinuteLedger",
                columns: new[] { "CustomerId", "StationTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMinuteLedger_PackageSaleId",
                table: "CustomerMinuteLedger",
                column: "PackageSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMinuteLedger_SessionId",
                table: "CustomerMinuteLedger",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMinuteLedger_StationTypeId",
                table: "CustomerMinuteLedger",
                column: "StationTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Phone",
                table: "Customers",
                column: "Phone",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageSales_CustomerId",
                table: "PackageSales",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageSales_TimePackageId",
                table: "PackageSales",
                column: "TimePackageId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PackageSaleId",
                table: "Payments",
                column: "PackageSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SessionId",
                table: "Payments",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TimePackages_Name",
                table: "TimePackages",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimePackages_StationTypeId",
                table: "TimePackages",
                column: "StationTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Customers_CustomerId",
                table: "Sessions",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Customers_CustomerId",
                table: "Sessions");

            migrationBuilder.DropTable(
                name: "CustomerMinuteLedger");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "PackageSales");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "TimePackages");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_CustomerId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "MemberHourlyRate",
                table: "StationTypes");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "IsMemberPricing",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                table: "RateRules");
        }
    }
}
