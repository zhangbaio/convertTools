using System.Text.Json;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public KuaishouAdxProjectContext? GetKuaishouAdxProjectContext()
    {
        if (SelectedPlatform.Value != PublishPlatform.KuaishouPersonalRevenue)
            return null;
        if (_activeGlobalAccount is null || SelectedJob is null)
            return null;
        var job = SelectedJob.Model;
        if (job.Platform != PublishPlatform.KuaishouPersonalRevenue || job.Kind != PublishJobKind.Series)
            return null;
        return new KuaishouAdxProjectContext(
            _activeGlobalAccount.Id, _activeGlobalAccount.Name,
            ResolveGlobalConfigPath(_activeGlobalAccount, PublishPlatform.KuaishouPersonalRevenue),
            ResolveAdxWorkflowDirectory(job.ProjectDirectory), SelectedJob.OriginalTitle, SelectedJob.NewTitle);
    }

    private static string ResolveAdxWorkflowDirectory(string projectDirectory)
    {
        try
        {
            var metadataPath = Path.Combine(projectDirectory, "shortdrama-project.json");
            if (!File.Exists(metadataPath)) return projectDirectory;
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (document.RootElement.TryGetProperty("workflowProjectDir", out var value) &&
                value.ValueKind == JsonValueKind.String && Directory.Exists(value.GetString()))
                return Path.GetFullPath(value.GetString()!);
        }
        catch { /* 元数据不可读时沿用任务目录。 */ }
        return projectDirectory;
    }

    public async Task QueueKuaishouAdxPublishAsync(KuaishouAdxPublishPayload payload,
        KuaishouAdxProjectContext context, bool autoStart)
    {
        if (payload.Items.Count == 0) throw new InvalidOperationException("没有可加入发布队列的快手 ADX 素材。");
        var job = new PublishJob
        {
            Platform = PublishPlatform.KuaishouPersonalRevenue,
            Kind = PublishJobKind.AdxMaterials,
            ProjectName = string.IsNullOrWhiteSpace(payload.NewTitle) ? payload.OriginalTitle : payload.NewTitle,
            ProjectDirectory = Path.GetFullPath(context.WorkflowDirectory),
            ConfigPath = context.ConfigPath,
            AccountId = context.AccountId,
            AccountName = context.AccountName,
            PublishCount = payload.Items.Count,
            CustomVideoFiles = payload.Items.Select(item => item.VideoPath).ToList(),
            PlatformOptionsJson = payload.ToJson(),
            Status = PublishJobStatus.Pending,
            StatusMessage = "ADX 素材已选择，等待快手宣发发布",
        };
        EnsureStepStates(job);
        _jobs.Add(job);
        await PersistAsync();
        RefreshVisibleJobs(job.Id);
        StatusMessage = $"已加入快手 ADX 素材任务：{job.ProjectName}，{payload.Items.Count} 条。";
        AppendActivityLog(StatusMessage);
        if (!autoStart) return;
        await RunRowsAsync([new PublishJobRowViewModel(job)], clearSchedule: true);
        RefreshVisibleJobs(job.Id);
    }

    public async Task QueueAdxPublishAsync(
        AdxPublishPayload payload,
        string workflowDirectory,
        string accountId,
        string accountName,
        string accountSessionDirectory,
        bool autoStart)
    {
        if (payload.Items.Count == 0) throw new InvalidOperationException("没有可加入发布队列的 ADX 素材。");
        var job = new PublishJob
        {
            Platform = PublishPlatform.WeixinChannel,
            Kind = PublishJobKind.AdxMaterials,
            ProjectName = string.IsNullOrWhiteSpace(payload.NewTitle) ? payload.OriginalTitle : payload.NewTitle,
            ProjectDirectory = Path.GetFullPath(workflowDirectory),
            AccountId = accountId,
            AccountName = accountName,
            AccountSessionDirectory = accountSessionDirectory,
            PublishCount = payload.Items.Count,
            CustomVideoFiles = payload.Items.Select(item => item.VideoPath).ToList(),
            PlatformOptionsJson = JsonSerializer.Serialize(payload),
            Status = PublishJobStatus.Pending,
            StatusMessage = "ADX 下载完成，等待发表",
        };
        EnsureStepStates(job);
        _jobs.Add(job);
        await PersistAsync();
        RefreshVisibleJobs(job.Id);
        StatusMessage = $"已加入 ADX 素材任务：{job.ProjectName}，{payload.Items.Count} 条。";
        AppendActivityLog(StatusMessage);
        if (!autoStart) return;
        var row = new PublishJobRowViewModel(job);
        await RunRowsAsync([row], clearSchedule: true);
        RefreshVisibleJobs(job.Id);
    }
}

public sealed record KuaishouAdxProjectContext(string AccountId, string AccountName, string ConfigPath,
    string WorkflowDirectory, string OriginalTitle, string NewTitle);
