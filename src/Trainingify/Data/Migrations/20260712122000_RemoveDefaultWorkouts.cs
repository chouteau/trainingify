using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Trainingify.Data.Migrations;

[DbContext(typeof(TrainingifyDbContext))]
[Migration("20260712122000_RemoveDefaultWorkouts")]
public sealed class RemoveDefaultWorkouts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM "Workouts"
            WHERE ("Id" = 1 AND "Name" = 'Dijon')
               OR ("Id" = 2 AND "Name" = 'Chili Pepper')
               OR ("Id" = 3 AND "Name" = 'Pasta')
               OR ("Id" = 4 AND "Name" = 'Potato Chips')
               OR ("Id" = 5 AND "Name" = '5-1-5 Moxy')
               OR ("Id" = 6 AND "Name" = 'Baguette')
               OR ("Id" = 7 AND "Name" = 'Baguette +1');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT OR IGNORE INTO "Workouts"
                ("Id", "Name", "Type", "DurationMinutes", "IntensityProfileJson", "IsFtpPercentage")
            VALUES
                (1, 'Dijon', 'VO2Max', 57, '[100,280,100,280,100,280,100,280,100]', 0),
                (2, 'Chili Pepper', 'VO2Max', 45, '[100,300,100,300,100,300,100]', 0),
                (3, 'Pasta', 'Threshold', 70, '[150,220,150,220,150]', 0),
                (4, 'Potato Chips', 'Threshold', 66, '[120,240,120,240,120]', 0),
                (5, '5-1-5 Moxy', 'Test', 66, '[100,150,200,250,300,100]', 0),
                (6, 'Baguette', 'Base', 90, '[130,130,130,130]', 0),
                (7, 'Baguette +1', 'Base', 90, '[140,140,140,140]', 0);
            """);
    }
}
