using CommunityToolkit.Mvvm.ComponentModel;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private bool _pipelineKuaishouDedupEnabled;
    private bool _loadingKuaishouDedupOption;

    private void LoadKuaishouDedupOption()
    {
        _loadingKuaishouDedupOption = true;
        try
        {
            PipelineKuaishouDedupEnabled = IsKuaishouPlatform && _activeGlobalAccount is { } account &&
                KuaishouVideoDedup.IsEnabled(account.Id);
        }
        finally
        {
            _loadingKuaishouDedupOption = false;
        }
    }

    partial void OnPipelineKuaishouDedupEnabledChanged(bool value)
    {
        if (!_loadingKuaishouDedupOption && IsKuaishouPlatform && _activeGlobalAccount is { } account)
        {
            try
            {
                KuaishouVideoDedup.SetEnabled(account.Id, value);
            }
            catch (Exception ex)
            {
                LoadKuaishouDedupOption();
                StatusMessage = $"保存快手去重步骤失败：{ex.Message}";
            }
        }
        NotifyCommands();
    }

    private static VideoDedupPreset? GetKuaishouDedupPreset(string accountId, KuaishouPersonalConfig config, bool enabled)
    {
        if (!enabled)
            return null;
        var preset = KuaishouVideoDedup.LoadPreset(accountId, config.VideoDedupTemplateId);
        KuaishouVideoDedup.Validate(preset);
        return preset;
    }

    private async Task<PublishVideoBatch> PrepareKuaishouDedupAsync(
        string projectDirectory, string projectName, KuaishouPersonalConfig config, KuaishouPipelineRequest request,
        PublishJobRowViewModel? row,
        CancellationToken cancellationToken)
    {
        var data = await _kuaishouPersonalProjectDataService.ResolveAsync(
            projectDirectory, config, cancellationToken, prepareVideos: false);
        void Log(string message) => AppendActivityLog($"视频去重：{message}", projectName);
        Log($"使用账号「{request.AccountName}」的去重方案。");
        PostUi(() => row?.SetVideoDedupProgress(0, 0));
        return await KuaishouVideoDedup.RunAsync(data, request.AccountId, request.AccountName, request.VideoDedupPreset!,
            request.Force, Log, _dramaDownloader.HasFfmpegSupportedVideoAsync, cancellationToken,
            (done, total) => PostUi(() => row?.SetVideoDedupProgress(done, total)));
    }
}
