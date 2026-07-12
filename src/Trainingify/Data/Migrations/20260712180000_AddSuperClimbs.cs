using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Trainingify.Data.Migrations;

[DbContext(typeof(TrainingifyDbContext))]
[Migration("20260712180000_AddSuperClimbs")]
public sealed class AddSuperClimbs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "SuperClimbs", columns: table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            Name = table.Column<string>(type: "TEXT", nullable: false), Region = table.Column<string>(type: "TEXT", nullable: false),
            Country = table.Column<string>(type: "TEXT", nullable: false), Description = table.Column<string>(type: "TEXT", nullable: false),
            ImageUrl = table.Column<string>(type: "TEXT", nullable: false), SourceUrl = table.Column<string>(type: "TEXT", nullable: false),
            GpxAsset = table.Column<string>(type: "TEXT", nullable: false), DistanceMeters = table.Column<double>(type: "REAL", nullable: false),
            ElevationGainMeters = table.Column<double>(type: "REAL", nullable: false), StartElevationMeters = table.Column<double>(type: "REAL", nullable: false),
            EndElevationMeters = table.Column<double>(type: "REAL", nullable: false), AverageGradePercent = table.Column<double>(type: "REAL", nullable: false),
            MaximumGradePercent = table.Column<double>(type: "REAL", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_SuperClimbs", x => x.Id));
        migrationBuilder.CreateTable(name: "SuperClimbSessions", columns: table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            SuperClimbId = table.Column<int>(type: "INTEGER", nullable: false), DistanceMeters = table.Column<double>(type: "REAL", nullable: false),
            DurationSeconds = table.Column<double>(type: "REAL", nullable: false), Status = table.Column<string>(type: "TEXT", nullable: false),
            UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_SuperClimbSessions", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_SuperClimbSessions_SuperClimbId_Status", table: "SuperClimbSessions", columns: new[] { "SuperClimbId", "Status" });
    }
    protected override void Down(MigrationBuilder migrationBuilder) { migrationBuilder.DropTable("SuperClimbSessions"); migrationBuilder.DropTable("SuperClimbs"); }
}
