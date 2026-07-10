using System;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace Trainingify.Data;

public class TrainingifyDbContext : DbContext
{
    public DbSet<UserProfile> UserProfiles { get; set; } = null!;
    public DbSet<Workout> Workouts { get; set; } = null!;
    public DbSet<CompletedSession> CompletedSessions { get; set; } = null!;
    public DbSet<ConnectedDevice> ConnectedDevices { get; set; } = null!;
    public DbSet<TrainingPlan> TrainingPlans { get; set; } = null!;
    public DbSet<TrainingPlanWorkout> TrainingPlanWorkouts { get; set; } = null!;

    public TrainingifyDbContext()
    {
    }

    public TrainingifyDbContext(DbContextOptions<TrainingifyDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            string appDataDir;
            try
            {
                appDataDir = FileSystem.AppDataDirectory;
            }
            catch
            {
                appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trainingify");
            }
            var dbPath = Path.Combine(appDataDir, "trainingify_local.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Seed some initial structured workouts for Trainingify
        modelBuilder.Entity<Workout>().HasData(
            new Workout { Id = 1, Name = "Dijon", Type = "VO2Max", DurationMinutes = 57, IntensityProfileJson = "[100,280,100,280,100,280,100,280,100]" },
            new Workout { Id = 2, Name = "Chili Pepper", Type = "VO2Max", DurationMinutes = 45, IntensityProfileJson = "[100,300,100,300,100,300,100]" },
            new Workout { Id = 3, Name = "Pasta", Type = "Threshold", DurationMinutes = 70, IntensityProfileJson = "[150,220,150,220,150]" },
            new Workout { Id = 4, Name = "Potato Chips", Type = "Threshold", DurationMinutes = 66, IntensityProfileJson = "[120,240,120,240,120]" },
            new Workout { Id = 5, Name = "5-1-5 Moxy", Type = "Test", DurationMinutes = 66, IntensityProfileJson = "[100,150,200,250,300,100]" },
            new Workout { Id = 6, Name = "Baguette", Type = "Base", DurationMinutes = 90, IntensityProfileJson = "[130,130,130,130]" },
            new Workout { Id = 7, Name = "Baguette +1", Type = "Base", DurationMinutes = 90, IntensityProfileJson = "[140,140,140,140]" }
        );

        // Seed initial profile
        modelBuilder.Entity<UserProfile>().HasData(
            new UserProfile { Id = 1, Name = "Cycliste", Weight = 75, Ftp = 256 }
        );
    }
}

public class UserProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "Cycliste";
    public double Weight { get; set; } = 75.0; // kg
    public double Ftp { get; set; } = 256.0;   // watts
}

public class Workout
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!; // e.g., VO2Max, Threshold, Test, Base
    public int DurationMinutes { get; set; }
    public string IntensityProfileJson { get; set; } = "[]"; // representation of interval levels
    public bool IsFtpPercentage { get; set; } = false;
}

public class CompletedSession
{
    public int Id { get; set; }
    public string WorkoutName { get; set; } = null!;
    public DateTime Date { get; set; }
    public double AveragePower { get; set; }
    public double MaxPower { get; set; }
    public double AverageHeartRate { get; set; }
    public double DurationSeconds { get; set; }
}

public class ConnectedDevice
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string DeviceType { get; set; } = null!; // Controllable, HRM, Moxy, CoreTemp
    public bool IsEnabled { get; set; }
    public string? Address { get; set; }
}

public class TrainingPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string GroupName { get; set; } = null!;
    public int CurrentWorkoutIndex { get; set; }
    public bool IsActive { get; set; }
    public List<TrainingPlanWorkout> Workouts { get; set; } = new();
}

public class TrainingPlanWorkout
{
    public int Id { get; set; }
    public int TrainingPlanId { get; set; }
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public int DurationMinutes { get; set; }
    public string IntensityProfileJson { get; set; } = null!;
    public bool IsCompleted { get; set; }
    public int SequenceOrder { get; set; }
    public string Path { get; set; } = null!;
}
