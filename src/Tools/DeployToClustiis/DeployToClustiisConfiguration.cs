namespace DeployToClustiis;

internal class DeployToClustiisConfiguration
{
    public string StagingUrl { get; set; } = null!;
    public string Environment { get; set; } = null!;
    public string RootPath { get; set; } = null!;
    public string ClustiisApiKey { get; set; } = null!;
    public string TeamsWebhookUrl { get; set; } = null!;
}
