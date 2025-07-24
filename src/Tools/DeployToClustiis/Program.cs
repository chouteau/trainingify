using System.Reflection;

using DeployToClustiis;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

Console.WriteLine("Deploy to Clustiis");
Console.WriteLine("-----------------");

var builder = WebApplication.CreateBuilder(args);

var env = args.GetParameterValue("--env");

var copyLocal = false;
if (!string.IsNullOrWhiteSpace(env))
{
	builder.Environment.EnvironmentName = env;

	builder.Configuration
		.AddJsonFile("appsettings.json")
		.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", true);
}
else
{
	copyLocal = true;
	builder.Configuration
	.AddJsonFile("appsettings.json")
	.AddJsonFile("appsettings.local.json", true);
}

var configuration = new DeployToClustiisConfiguration();
builder.Configuration.GetSection("DeployToClustiis").Bind(configuration);

var location = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
configuration.RootPath = System.IO.Path.Combine(location, configuration.RootPath);
var directory = new System.IO.DirectoryInfo(configuration.RootPath);

System.Diagnostics.Debug.WriteLine(directory.FullName);
Console.WriteLine(directory.FullName);

var projectListToDeploy = new List<Project>()
{
	new Project
	{
		Name = "Trainingify",
		CsprojFileName = System.IO.Path.Combine(directory.FullName, "Trainingify\\Trainingify.csproj"),
		PublishPath = System.IO.Path.Combine(directory.FullName, "Trainingify\\bin\\debug\\net9.0\\publish"),
		ClientName = "Trainingify"
	}
};

foreach (var project in projectListToDeploy)
{
	Helpers.Process("dotnet", @$"publish -c Debug {project.CsprojFileName}");

	var envTxtFileName = System.IO.Path.Combine(project.PublishPath, $"env.txt");
	await System.IO.File.WriteAllTextAsync(envTxtFileName, configuration.Environment);

	// On supprime le fichier appsettings.local.json 
	string excludeInZip = string.Empty;
	if (!copyLocal)
	{
		var appSettingsLocalFileName = System.IO.Path.Combine(project.PublishPath, $"appsettings.local.json");
		if (System.IO.File.Exists(appSettingsLocalFileName))
		{
			System.IO.File.Delete(appSettingsLocalFileName);
		}
		excludeInZip = "-x!appsettings.local.json";
	}

	// Supprimer le fichier zip
	var zipFileName = System.IO.Path.Combine(project.PublishPath, "..\\", $"publish.zip");
	if (System.IO.File.Exists(zipFileName))
	{
		System.IO.File.Delete(zipFileName);
	}
	Helpers.Process(@"""C:\Program Files\7-Zip\7z.exe""", @$"a -tzip -r {project.PublishPath} *", project.PublishPath);

	var fileInfo = new System.IO.FileInfo(zipFileName);

	using var form = new MultipartFormDataContent();
	form.Headers.ContentType!.MediaType = "multipart/form-data";

	var content = new FileStream(zipFileName, FileMode.Open);
	var stream = new StreamContent(content, (int)fileInfo.Length);
	form.Add(stream, $"{project.Name}.zip", $"{project.Name}.zip");

	using var httpClient = new HttpClient();
	httpClient.BaseAddress = new Uri(configuration.StagingUrl);
	httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("BASIC", configuration.ClustiisApiKey);

	Console.WriteLine("try to upload to {0}/api/upload-package", configuration.StagingUrl);
	var response = await httpClient.PostAsync("/api/upload-package", form);
	response.EnsureSuccessStatusCode();

	//// Envoyer un message de publication dans teams
	//var message = new StringBuilder();

	//message.AppendLine($"# Publication de **{project.Name}** sur **{configuration.Environment}**");
	//message.AppendLine($"- Url : {configuration.StagingUrl}");
	//message.AppendLine($"- Client : {project.ClientName}");
	//message.AppendLine($"- Environnement : {configuration.Environment}");
	//message.AppendLine($"- Date : {DateTime.Now}");

	//var client = new HttpClient();
	//var payload = new { text = message.ToString() };
	//var json = System.Text.Json.JsonSerializer.Serialize(payload);
	//var contentMessage = new StringContent(json, Encoding.UTF8, "application/json");
	//var responseMessage = await client.PostAsync(configuration.TeamsWebhookUrl, contentMessage);
	//responseMessage.EnsureSuccessStatusCode();
}