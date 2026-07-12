using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Trainingify.Data.Migrations;

[DbContext(typeof(TrainingifyDbContext))]
[Migration("20260712121000_AddFitActivityToCompletedSession")]
public sealed class AddFitActivityToCompletedSession : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "FitFileData",
            table: "CompletedSessions",
            type: "BLOB",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FitFileName",
            table: "CompletedSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "FtpAtCompletion",
            table: "CompletedSessions",
            type: "REAL",
            nullable: false,
            defaultValue: 0d);

        migrationBuilder.AddColumn<double>(
            name: "NormalizedPower",
            table: "CompletedSessions",
            type: "REAL",
            nullable: false,
            defaultValue: 0d);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "FitFileData", table: "CompletedSessions");
        migrationBuilder.DropColumn(name: "FitFileName", table: "CompletedSessions");
        migrationBuilder.DropColumn(name: "FtpAtCompletion", table: "CompletedSessions");
        migrationBuilder.DropColumn(name: "NormalizedPower", table: "CompletedSessions");
    }
}
