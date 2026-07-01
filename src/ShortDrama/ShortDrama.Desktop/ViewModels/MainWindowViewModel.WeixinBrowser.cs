using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly string[] WeixinMaterialUploadConfigNames =
    [
        "weixin-channel-publish-test.json",
        "weixin-channel-publish.json",
        "weixin-channel-material.json"
    ];

    [ObservableProperty]
    private bool isWeixinBrowserSessionRunning;

    partial void OnIsWeixinBrowserSessionRunningChanged(bool value) => RefreshCommandStates();

    private bool CanOpenWeixinBrowser()
    {
        return !IsWeixinBrowserSessionRunning &&
               (!string.IsNullOrWhiteSpace(RootDir) || SelectedProject is not null);
    }

    private Task OpenWeixinBrowserAsync()
    {
        if (!CanOpenWeixinBrowser())
        {
            return Task.CompletedTask;
        }

        var (projectDir, configPath, displayName) = ResolveWeixinBrowserLaunchTarget();
        IsWeixinBrowserSessionRunning = true;
        AppendLog(
            $"打开微信浏览器：{displayName}",
            SelectedProject?.ProjectKey ?? string.Empty,
            SelectedProject?.DisplayName ?? displayName,
            "weixin-browser",
            "打开浏览器");

        _ = Task.Run(async () =>
        {
            try
            {
                await _weixinBrowserSessionLauncher.OpenHomeAsync(configPath, projectDir, CancellationToken.None);
                Dispatcher.UIThread.Post(() =>
                {
                    AppendLog(
                        "微信浏览器会话已结束。",
                        SelectedProject?.ProjectKey ?? string.Empty,
                        SelectedProject?.DisplayName ?? displayName,
                        "weixin-browser",
                        "打开浏览器");
                    IsWeixinBrowserSessionRunning = false;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    AppendLog(
                        $"打开微信浏览器失败：{ex.Message}",
                        SelectedProject?.ProjectKey ?? string.Empty,
                        SelectedProject?.DisplayName ?? displayName,
                        "weixin-browser",
                        "打开浏览器",
                        isFailure: true);
                    StatusMessage = ex.Message;
                    IsWeixinBrowserSessionRunning = false;
                });
            }
        });

        return Task.CompletedTask;
    }

    private (string ProjectDir, string? ConfigPath, string DisplayName) ResolveWeixinBrowserLaunchTarget()
    {
        if (SelectedProject is not null)
        {
            var workflowDir = string.IsNullOrWhiteSpace(SelectedProject.WorkflowProjectDir)
                ? SelectedProject.SourceProjectDir
                : SelectedProject.WorkflowProjectDir!;
            var configPath = ResolvePreferredWeixinBrowserConfigPath(SelectedProject);
            return (workflowDir, configPath, SelectedProject.DisplayName);
        }

        return (RootDir, null, string.IsNullOrWhiteSpace(RootDir) ? "微信视频号后台首页" : RootDir);
    }

    private string? ResolvePreferredWeixinBrowserConfigPath(ProjectListItemViewModel project)
    {
        var preferMaterial = string.Equals(TaskQueueDetailMode, TaskQueueDetailMaterialUpload, StringComparison.Ordinal);
        var primary = preferMaterial
            ? ResolveWeixinMaterialUploadConfigPath(project)
            : ResolveWeixinUploadConfigPath(project);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        var secondary = preferMaterial
            ? ResolveWeixinUploadConfigPath(project)
            : ResolveWeixinMaterialUploadConfigPath(project);
        if (!string.IsNullOrWhiteSpace(secondary))
        {
            return secondary;
        }

        return null;
    }

    private static string? ResolveWeixinMaterialUploadConfigPath(ProjectListItemViewModel project)
    {
        foreach (var name in WeixinMaterialUploadConfigNames)
        {
            var candidate = Path.Combine(project.WorkflowProjectDir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
