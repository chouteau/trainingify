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
    public DbSet<SuperClimb> SuperClimbs { get; set; } = null!;
    public DbSet<SuperClimbSession> SuperClimbSessions { get; set; } = null!;

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

        // Seed initial profile
        modelBuilder.Entity<UserProfile>().HasData(
            new UserProfile { Id = 1, Name = "Cycliste", Weight = 75, Ftp = 250 }
        );
    }
}

public class UserProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "Cycliste";
    public double Weight { get; set; } = 75.0; // kg
    public double Ftp { get; set; } = 250.0;   // watts
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
    public double NormalizedPower { get; set; }
    public double FtpAtCompletion { get; set; }
    public string? FitFileName { get; set; }
    public byte[]? FitFileData { get; set; }
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

public class SuperClimb
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Region { get; set; } = null!;
    public string Country { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string ImageUrl { get; set; } = null!;
    public string SourceUrl { get; set; } = null!;
    public string GpxAsset { get; set; } = null!;
    public double DistanceMeters { get; set; }
    public double ElevationGainMeters { get; set; }
    public double StartElevationMeters { get; set; }
    public double EndElevationMeters { get; set; }
    public double AverageGradePercent { get; set; }
    public double MaximumGradePercent { get; set; }
}

public class SuperClimbSession
{
    public int Id { get; set; }
    public int SuperClimbId { get; set; }
    public double DistanceMeters { get; set; }
    public double DurationSeconds { get; set; }
    public string Status { get; set; } = "Saved";
    public DateTime UpdatedAtUtc { get; set; }
}
