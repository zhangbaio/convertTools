namespace ShortDrama.Core.Interfaces;

public interface IWeixinBrowserSessionLauncher
{
    Task OpenHomeAsync(
        string? configPath,
        string projectDir,
        CancellationToken cancellationToken);
}
