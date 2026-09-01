using System.Text.Json;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Desktop.Services;

public sealed class WeixinWorkflowSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path = Path.Combine(PlatformPublisherPaths.DataRoot, "weixin-workflow-settings.json");

    public async Task<WeixinWorkflowSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new WeixinWorkflowSettings();
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<WeixinWorkflowSettings>(stream, JsonOptions, cancellationToken)
                   ?? new WeixinWorkflowSettings();
        }
        catch
        {
            return new WeixinWorkflowSettings();
        }
    }

    public async Task SaveAsync(WeixinWorkflowSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        File.Move(tempPath, _path, overwrite: true);
    }
}

public sealed class WeixinWorkflowSettings
{
    public string LastWorkspaceDirectory { get; set; } = string.Empty;
    public string ArchiveRootDirectory { get; set; } = string.Empty;
    public bool DownloadEnabled { get; set; }
    public bool RewriteEnabled { get; set; } = true;
    public bool PosterEnabled { get; set; } = true;
    public bool TranscodeEnabled { get; set; } = true;
    public bool AutoRepairEnabled { get; set; } = true;
    public bool AutoFillEnabled { get; set; } = true;
    public bool CostReportEnabled { get; set; } = true;
    public bool ProjectImageEnabled { get; set; } = true;
    public bool MaterialValidateEnabled { get; set; } = true;
    public bool RemuxEnabled { get; set; }
    public bool ForceRerun { get; set; }
    public bool AutoArchiveAfterUpload { get; set; }
    public bool PreferUploadWhenReady { get; set; } = true;
    public int PageSize { get; set; } = 20;
    public int AutoShelfMaxPages { get; set; } = 10;
    public int AutoShelfMaxRounds { get; set; } = 20;
    public bool SmartRecutEnabled { get; set; }
    public int SmartRecutEpisodeCount { get; set; }
    public int SmartRecutMinSeconds { get; set; } = 60;
    public int SmartRecutMaxSeconds { get; set; } = 180;
}
