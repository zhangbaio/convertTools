using System.Text.Json;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
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
