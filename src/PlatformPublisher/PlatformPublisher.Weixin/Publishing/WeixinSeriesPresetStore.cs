using System.Security.Cryptography;
using System.Text;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinSeriesPresetStore(AccountJsonSettingStore store)
{
    private const string PresetsKey = "weixin.series.presets";
    private const string AccountFormKey = "weixin.series.account-form";

    public Dictionary<string, string> LoadAccountForm(string accountId)
    {
        if (store.TryLoad<Dictionary<string, string>>(accountId, AccountFormKey, out var values) && values is not null)
            return values;

        var legacy = Load(accountId);
        return legacy.DefaultId.Length == 0
            ? []
            : legacy.Presets.FirstOrDefault(preset => preset.Id == legacy.DefaultId)?.Values ?? [];
    }

    public void SaveAccountForm(string accountId, Dictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        store.Save(accountId, AccountFormKey, values);
    }

    public WeixinSeriesPresetCollection Load(string accountId) =>
        store.TryLoad<WeixinSeriesPresetCollection>(accountId, PresetsKey, out var presets) && presets is not null
            ? presets : new();

    public void Save(string accountId, WeixinSeriesPresetCollection presets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        store.Save(accountId, PresetsKey, presets);
    }

    public WeixinSeriesProjectForm? LoadProject(string accountId, string projectDirectory)
    {
        if (!store.TryLoad<WeixinSeriesProjectForm>(accountId, ProjectKey(projectDirectory), out var projectForm) ||
            projectForm is null)
            return null;

        var preset = projectForm.PresetSnapshot;
        if (preset is null && !string.IsNullOrEmpty(projectForm.PresetId))
            preset = Load(accountId).Presets.FirstOrDefault(item => item.Id == projectForm.PresetId);
        if (preset is null)
            return projectForm;

        // 已保存项目选用的旧方案转换为该项目的覆盖值，项目明确填写的字段优先。
        var values = new Dictionary<string, string>(preset.Values);
        foreach (var (key, value) in projectForm.Values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values[key] = value;
        }
        return new WeixinSeriesProjectForm("", values, projectForm.ConfigPath);
    }

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
