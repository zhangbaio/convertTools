using CommunityToolkit.Mvvm.ComponentModel;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Weixin.Publishing;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly WeixinStoryboardRecordingService _storyboardRecordingService;

    [ObservableProperty] private bool _pipelineStoryboardRecordingEnabled;
    [ObservableProperty] private int _storyboardTemplateIndex;
    [ObservableProperty] private string _storyboardEpisodes = "";
    [ObservableProperty] private bool _storyboardArkEnabled;

    partial void OnPipelineStoryboardRecordingEnabledChanged(bool value) => OnWorkflowStepChanged();

    private StoryboardRecordingOptions GetStoryboardOptions() =>
        new(StoryboardEpisodes, StoryboardTemplateIndex == 1 ? StoryboardTemplate.ThreeView : StoryboardTemplate.Comprehensive,
            StoryboardArkEnabled, PipelineForceRerun);

    private async Task RunStoryboardStepAsync(string directory, PublishJob? job, PublishJobRowViewModel? row,
        StoryboardRecordingOptions options, CancellationToken cancellationToken)
    {
        var retrying = job is { Status: PublishJobStatus.Failed } &&
            job.StepStates.GetValueOrDefault(WeixinStoryboardRecordingService.StepKey)?.Status == PublishJobStepStatus.Failed;
        await RunTrackedStepAsync(job, row, WeixinStoryboardRecordingService.StepKey, WeixinStoryboardRecordingService.StepLabel,
            options.Force, async () =>
            {
                StoryboardArkSettings? ark = null;
                if (options.UseArk)
                {
                    var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
                    ark = new StoryboardArkSettings(settings.AiTextEndpoint, settings.AiTextModel, settings.AiTextApiKey,
                        settings.ImageModelEndpoint, settings.ImageModelId, settings.ImageModelApiKey);
                }
                var projectName = job?.ProjectName ?? Path.GetFileName(directory);
                await _storyboardRecordingService.GenerateAsync(directory, projectName, options, ark,
                    new Progress<string>(message =>
                    {
                        SetStatusMessage($"[{projectName}] {message}");
                        AppendActivityLog(message, projectName);
                    }), cancellationToken);
            });
        if (retrying && job is not null && job.StepStates.Values.All(step => step.Status != PublishJobStepStatus.Failed))
        {
            job.Status = job.StepStates.GetValueOrDefault("weixin-upload")?.Status == PublishJobStepStatus.Succeeded
                ? PublishJobStatus.Succeeded : PublishJobStatus.Pending;
            job.StatusMessage = "分镜录屏已完成";
            await PersistAsync(cancellationToken);
            row?.Refresh();
        }
    }

    private static bool IsStoryboardJob(PublishJob job) =>
        job.Platform == PublishPlatform.WeixinChannel && job.Kind == PublishJobKind.Series;

    private static bool IsStoryboardFailure(PublishJobRowViewModel row) =>
        IsStoryboardJob(row.Model) &&
        row.Model.StepStates.GetValueOrDefault(WeixinStoryboardRecordingService.StepKey)?.Status == PublishJobStepStatus.Failed;

    private async Task<bool> RetryStoryboardRowsAsync(PublishJobRowViewModel[] rows)
    {
        var options = GetStoryboardOptions();
        var completed = false;
        try
        {
            await RunBusyAsync(async cancellationToken =>
            {
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _activityLogProject = row.ProjectName;
                    try
                    {
                        await RunStoryboardStepAsync(row.Model.ProjectDirectory, row.Model, row, options, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppendActivityLog($"[{row.ProjectName}] 分镜录屏重试失败：{ex.Message}");
                    }
                    finally { _activityLogProject = ""; }
                }
                completed = true;
            });
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "分镜录屏重试已停止。";
            AppendActivityLog(StatusMessage);
        }
        return completed;
    }
}
