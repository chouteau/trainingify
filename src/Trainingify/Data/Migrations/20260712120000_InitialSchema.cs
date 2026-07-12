using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Trainingify.Data.Migrations;

[DbContext(typeof(TrainingifyDbContext))]
[Migration("20260712120000_InitialSchema")]
public sealed class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // IF NOT EXISTS permet d'adopter sans perte les bases historiques créées
        // avant l'introduction des migrations EF Core dans l'application.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "UserProfiles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UserProfiles" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Weight" REAL NOT NULL,
                "Ftp" REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "Workouts" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Workouts" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Type" TEXT NOT NULL,
                "DurationMinutes" INTEGER NOT NULL,
                "IntensityProfileJson" TEXT NOT NULL,
                "IsFtpPercentage" INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS "CompletedSessions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CompletedSessions" PRIMARY KEY AUTOINCREMENT,
                "WorkoutName" TEXT NOT NULL,
                "Date" TEXT NOT NULL,
                "AveragePower" REAL NOT NULL,
                "MaxPower" REAL NOT NULL,
                "AverageHeartRate" REAL NOT NULL,
                "DurationSeconds" REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "ConnectedDevices" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ConnectedDevices" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "DeviceType" TEXT NOT NULL,
                "IsEnabled" INTEGER NOT NULL,
                "Address" TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS "TrainingPlans" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TrainingPlans" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "GroupName" TEXT NOT NULL,
                "CurrentWorkoutIndex" INTEGER NOT NULL,
                "IsActive" INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "TrainingPlanWorkouts" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TrainingPlanWorkouts" PRIMARY KEY AUTOINCREMENT,
                "TrainingPlanId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "DurationMinutes" INTEGER NOT NULL,
                "IntensityProfileJson" TEXT NOT NULL,
                "IsCompleted" INTEGER NOT NULL,
                "SequenceOrder" INTEGER NOT NULL,
                "Path" TEXT NOT NULL,
                CONSTRAINT "FK_TrainingPlanWorkouts_TrainingPlans_TrainingPlanId"
                    FOREIGN KEY ("TrainingPlanId") REFERENCES "TrainingPlans" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_TrainingPlanWorkouts_TrainingPlanId"
                ON "TrainingPlanWorkouts" ("TrainingPlanId");
            INSERT OR IGNORE INTO "UserProfiles" ("Id", "Name", "Weight", "Ftp")
                VALUES (1, 'Cycliste', 75.0, 250.0);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TrainingPlanWorkouts");
        migrationBuilder.DropTable(name: "CompletedSessions");
        migrationBuilder.DropTable(name: "ConnectedDevices");
        migrationBuilder.DropTable(name: "Workouts");
        migrationBuilder.DropTable(name: "TrainingPlans");
        migrationBuilder.DropTable(name: "UserProfiles");
    }
}
