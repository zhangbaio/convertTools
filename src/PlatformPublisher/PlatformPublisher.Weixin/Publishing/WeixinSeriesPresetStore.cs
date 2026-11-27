using System.Security.Cryptography;
using System.Text;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinSeriesPresetStore(AccountJsonSettingStore store)
{
    private const string PresetsKey = "weixin.series.presets";

    public WeixinSeriesPresetCollection Load(string accountId) =>
        store.TryLoad<WeixinSeriesPresetCollection>(accountId, PresetsKey, out var presets) && presets is not null
            ? presets : new();

    public void Save(string accountId, WeixinSeriesPresetCollection presets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        store.Save(accountId, PresetsKey, presets);
    }

    public WeixinSeriesProjectForm? LoadProject(string accountId, string projectDirectory) =>
        store.TryLoad<WeixinSeriesProjectForm>(accountId, ProjectKey(projectDirectory), out var projectForm) ? projectForm : null;

    public void SaveProject(string accountId, string projectDirectory, WeixinSeriesProjectForm form)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        store.Save(accountId, ProjectKey(projectDirectory), form);
    }

    private static string ProjectKey(string projectDirectory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return "weixin.series.project." + Convert.ToHexString(hash);
    }
}
