using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalUploadState
{
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "pending";
    public string CurrentStage { get; set; } = "pending";
    public string LastError { get; set; } = string.Empty;
    public string MiniSeriesId { get; set; } = string.Empty;
    public bool FirstPageCompleted { get; set; }
    public bool EpisodeInfoCompleted { get; set; }
    public bool VideosUploaded { get; set; }
    public bool ReviewSubmitted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KuaishouPersonalUploadStateStore
{
    private const string FileName = ".kuaishou-personal-upload-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public KuaishouPersonalUploadState Load(string workflowDirectory)
    {
        var path = GetPath(workflowDirectory);
        if (!File.Exists(path)) return new KuaishouPersonalUploadState();
        try
        {
            return JsonSerializer.Deserialize<KuaishouPersonalUploadState>(File.ReadAllText(path), JsonOptions)
                   ?? new KuaishouPersonalUploadState();
        }
        catch (JsonException)
        {
            return new KuaishouPersonalUploadState
            {
                Status = "failed",
                CurrentStage = "state_load",
                LastError = "状态文件格式损坏，已忽略并重新执行。",
            };
        }
    }

    public async Task SaveAsync(
        string workflowDirectory,
        KuaishouPersonalUploadState state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workflowDirectory);
        state.UpdatedAt = DateTimeOffset.Now;
        var path = GetPath(workflowDirectory);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    public static string GetPath(string workflowDirectory) => Path.Combine(workflowDirectory, FileName);
}
