using Microsoft.Extensions.Logging;
using Trainingify.Data;
using Trainingify.Services;

namespace Trainingify;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddDbContextFactory<TrainingifyDbContext>();
		builder.Services.AddSingleton<TrainingStateService>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		// Initialize Database and Seed Data
		using (var scope = app.Services.CreateScope())
		{
			var dbContext = scope.ServiceProvider.GetRequiredService<TrainingifyDbContext>();
			string appDataDir;
			try
			{
				appDataDir = FileSystem.AppDataDirectory;
			}
			catch
			{
				appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trainingify");
			}
			if (!Directory.Exists(appDataDir))
			{
				Directory.CreateDirectory(appDataDir);
			}
			dbContext.Database.EnsureCreated();
		}

		return app;
	}
}
