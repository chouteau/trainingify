namespace DeployToClustiis;

internal class Project
{
	public string Name { get; set; } = null!;
	public string CsprojFileName { get; set; } = null!;
	public string PublishPath { get; set; } = null!;
	public string ClientName { get; set; } = null!;
}
