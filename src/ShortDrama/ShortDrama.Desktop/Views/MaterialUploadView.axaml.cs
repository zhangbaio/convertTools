using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChannelsPublisher.Clip;
using Microsoft.Extensions.DependencyInjection;
using ShortDrama.Desktop.Services;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views;

public partial class MaterialUploadView : UserControl
{
    public MaterialUploadView()
    {
        InitializeComponent();

        RunMaterialUploadQueueButton.Click += RunMaterialUploadQueueButton_Click;
        CheckAllVisibleButton.Click += (_, _) => ViewModel?.SetAllMaterialUploadProjectsChecked(true);
        UncheckAllVisibleButton.Click += (_, _) => ViewModel?.SetAllMaterialUploadProjectsChecked(false);
        OpenPublishConfigButton.Click += OpenPublishConfigButton_Click;
        OpenMaterialUploadToolbarBrowserButton.Click += async (_, _) => await OpenSelectedMaterialUploadAccountBrowserAsync(relogin: false);
        OpenMaterialRuntimeControlsButton.Click += OpenMaterialRuntimeControlsButton_Click;
        PublishSystemHighlightButton.Click += PublishSystemHighlightButton_Click;
        DownloadSystemHighlightButton.Click += async (_, _) => await OpenPublishConfigWithSourceAsync("downloaded_system_highlight");
        DownloadMaterialVideoButton.Click += async (_, _) => await OpenPublishConfigWithSourceAsync("material_video_download");
        DirectoryBatchPublishButton.Click += DirectoryBatchPublishButton_Click;
        OpenSystemHighlightScheduleButton.Click += OpenSystemHighlightScheduleButton_Click;
        ShowMaterialLogsButton.Click += ShowMaterialLogsButton_Click;
        CreateManualMaterialProjectButton.Click += CreateManualMaterialProjectButton_Click;
        DeleteChannelMaterialsButton.Click += DeleteChannelMaterialsButton_Click;
        OpenClipConfigButton.Click += OpenClipConfigButton_Click;
        GenerateClipsFullEngineButton.Click += GenerateClipsFullEngineButton_Click;
        AddMaterialUploadAccountButton.Click += (_, _) => ViewModel?.AddMaterialUploadAccount();
        RenameMaterialUploadAccountButton.Click += (_, _) => ViewModel?.SaveSelectedMaterialUploadAccountConfig();
        DeleteMaterialUploadAccountButton.Click += (_, _) => ViewModel?.DeleteSelectedMaterialUploadAccount();
        SetCurrentMaterialUploadAccountButton.Click += (_, _) => ViewModel?.SetSelectedMaterialUploadAccountActive();
        LoginMaterialUploadAccountButton.Click += async (_, _) => await OpenSelectedMaterialUploadAccountBrowserAsync(relogin: false);
        ReloginMaterialUploadAccountButton.Click += async (_, _) => await OpenSelectedMaterialUploadAccountBrowserAsync(relogin: true);
        OpenMaterialUploadAccountBrowserButton.Click += async (_, _) => await OpenSelectedMaterialUploadAccountBrowserAsync(relogin: false);
        BindCheckedMaterialUploadAccountButton.Click += (_, _) => ViewModel?.BindCheckedMaterialUploadProjectsToSelectedAccount();
        BindCurrentMaterialUploadAccountButton.Click += (_, _) => ViewModel?.BindCurrentMaterialUploadProjectToSelectedAccount();
        ClearMaterialUploadAccountBindingButton.Click += (_, _) => ViewModel?.ClearMaterialUploadProjectAccountBinding();
        SaveMaterialUploadAccountButton.Click += (_, _) => ViewModel?.SaveSelectedMaterialUploadAccountConfig();
        BrowseMaterialUploadAuthFileButton.Click += BrowseMaterialUploadAuthFileButton_Click;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private async void PickRootDir_Click(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow?.StorageProvider is null)
        {
            return;
        }

        var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            ViewModel?.SetRootDir(folder.Path.LocalPath);
        }
    }

    private async void RunMaterialUploadQueueButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            DesktopCrashTrace.Write("material-upload queue click begin");
            await ViewModel.RunCheckedMaterialUploadQueueFromPageAsync();
            DesktopCrashTrace.Write("material-upload queue click complete");
        }
        catch (Exception ex)
        {
            DesktopCrashTrace.Write($"material-upload queue click exception: {ex}");
            ReportMaterialUploadRunError("发表素材失败", ex, null);
        }
    }

    private async void OpenPublishConfigButton_Click(object? sender, RoutedEventArgs e)
    {
        await OpenPublishConfigWithSourceAsync(null);
    }

    private async Task OpenPublishConfigWithSourceAsync(string? preferredSourceMode)
    {
        if (ViewModel is null || OwnerWindow is null)
        {
            return;
        }

        var target = ViewModel.ResolveMaterialPublishConfigTarget(null);
        if (target is null)
        {
            return;
        }

        bool accepted;
        try
        {
            var window = new MaterialPublishConfigWindow(
                target.ConfigPath,
                target.Project.DisplayName,
                preferredSourceMode);
            accepted = await window.ShowDialog<bool>(OwnerWindow);
        }
        catch (Exception ex)
        {
            ViewModel.AppendExternalLog(
                $"打开素材发表配置失败：{ex.Message}",
                target.Project.ProjectKey,
                target.Project.DisplayName,
                "material-upload",
                "素材上传",
                isFailure: true);
            ViewModel.StatusMessage = $"打开素材发表配置失败：{ex.Message}";
            return;
        }

        if (!accepted)
        {
            return;
        }

        try
        {
            DesktopCrashTrace.Write("material-publish-config dialog accepted; append log begin");
            ViewModel.AppendExternalLog(
                $"已保存素材发表配置：{target.Project.DisplayName}",
                target.Project.ProjectKey,
                target.Project.DisplayName,
                "material-upload",
                "素材上传");
            ViewModel.StatusMessage = $"已保存素材发表配置：{target.Project.DisplayName}";
            DesktopCrashTrace.Write("material-publish-config dialog accepted; refresh current project summary");
            ViewModel.RefreshMaterialPublishConfigAfterSave(target.Project);
            DesktopCrashTrace.Write("material-publish-config dialog accepted; refresh complete");
        }
        catch (Exception ex)
        {
            DesktopCrashTrace.Write($"material-publish-config dialog accepted exception: {ex}");
            ViewModel.StatusMessage = $"刷新素材发表配置失败：{ex.Message}";
            ViewModel.AppendExternalLog(
                ViewModel.StatusMessage,
                target.Project.ProjectKey,
                target.Project.DisplayName,
                "material-upload",
                "素材上传",
                isFailure: true);
        }
    }

    private void OpenMaterialRuntimeControlsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.ShowMaterialUploadLogs(ViewModel.SelectedProject);
        ViewModel.StatusMessage = ViewModel.HasInteractionRequest
            ? "素材流程等待人工处理，可在日志页使用接管、继续、跳过或停止。"
            : "当前没有等待人工处理的素材流程；运行中任务可使用停止。";
    }

    private async void PublishSystemHighlightButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || OwnerWindow is null)
        {
            return;
        }

        var window = new MaterialSystemHighlightBatchPublishWindow();
        var result = await window.ShowDialog<MaterialSystemHighlightBatchPublishDialogResult?>(OwnerWindow);
        if (result is null)
        {
            return;
        }

        await ViewModel.RunMaterialSystemHighlightBatchPublishAsync(result);
    }

    private async void OpenSystemHighlightScheduleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || OwnerWindow is null || Application.Current is not App app)
        {
            return;
        }

        var service = app.Services.GetRequiredService<MaterialSystemHighlightScheduleService>();
        var window = new MaterialSystemHighlightScheduleWindow(service, ViewModel.RootDir, ViewModel.VisibleMaterialUploadAccounts);
        var result = await window.ShowDialog<MaterialSystemHighlightScheduleDialogResult?>(OwnerWindow);
        if (result is null)
        {
            return;
        }

        await ViewModel.HandleMaterialSystemHighlightScheduleDialogResultAsync(result);
    }

    private async Task OpenSelectedMaterialUploadAccountBrowserAsync(bool relogin)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.OpenSelectedMaterialUploadAccountBrowserAsync(relogin);
    }

    private async void DirectoryBatchPublishButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || OwnerWindow is null)
        {
            return;
        }

        var initialDirectory = Directory.Exists(ViewModel.RootDir) ? ViewModel.RootDir : string.Empty;
        var window = new MaterialDirectoryPublishWindow(initialDirectory);
        var accepted = await window.ShowDialog<bool>(OwnerWindow);
        if (!accepted)
        {
            return;
        }

        await ViewModel.RunMaterialDirectoryBatchPublishAsync(
            window.WorkspacePath,
            window.HideLocation,
            window.DeclareOriginal,
            window.AiRewriteDescription);
    }

    private async void BrowseMaterialUploadAuthFileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow?.StorageProvider is null)
        {
            return;
        }

        var files = await OwnerWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择授权文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"]
                },
                FilePickerFileTypes.All
            ]
        });

        var file = files.FirstOrDefault();
        if (file is not null)
        {
            ViewModel?.SetSelectedMaterialUploadAccountAuthFile(file.Path.LocalPath);
        }
    }

    private async void RunSingleMaterialUpload_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var project = (sender as Control)?.DataContext as ProjectListItemViewModel;
        try
        {
            await ViewModel.RunMaterialUploadProjectFromPageAsync(project);
        }
        catch (Exception ex)
        {
            ReportMaterialUploadRunError("发表素材失败", ex, project);
        }
    }

    private void ReportMaterialUploadRunError(
        string prefix,
        Exception exception,
        ProjectListItemViewModel? project)
    {
        if (ViewModel is null)
        {
            return;
        }

        var message = $"{prefix}：{exception.Message}";
        ViewModel.StatusMessage = message;
        ViewModel.AppendExternalLog(
            message,
            project?.ProjectKey ?? string.Empty,
            project?.DisplayName ?? string.Empty,
            "material-upload",
            "素材上传",
            isFailure: true);
    }

    private void ShowMaterialLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ShowMaterialUploadLogs(ViewModel.SelectedProject);
    }

    private async void CreateManualMaterialProjectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || OwnerWindow is null || Application.Current is not App app)
        {
            return;
        }

        var service = app.Services.GetRequiredService<ManualMaterialProjectService>();
        var window = new ManualMaterialProjectWindow(ViewModel.RootDir, service);
        var accepted = await window.ShowDialog<bool>(OwnerWindow);
        if (!accepted || string.IsNullOrWhiteSpace(window.VideoDirectory))
        {
            return;
        }

        try
        {
            var result = service.CreateProject(new ManualMaterialProjectRequest(
                ViewModel.RootDir,
                window.VideoDirectory!,
                window.NewTitle,
                window.OriginalTitle,
                window.EpisodeCount));

            ViewModel.AppendExternalLog(result.Message, stepKey: "material-upload", stepLabel: "素材上传");
            ViewModel.StatusMessage = result.Message;
            await ViewModel.ScanCommand.ExecuteAsync(null);

            var target = ViewModel.MaterialUploadProjects.FirstOrDefault(project =>
                string.Equals(project.SourceProjectDir, result.SourceProjectDirectory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(project.WorkflowProjectDir, result.WorkflowProjectDirectory, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                ViewModel.ActivateMaterialUploadProject(target);
            }
        }
        catch (Exception ex)
        {
            ViewModel.AppendExternalLog(
                $"手动创建素材项目失败：{ex.Message}",
                stepKey: "material-upload",
                stepLabel: "素材上传",
                isFailure: true);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async void DeleteChannelMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null || OwnerWindow is null || Application.Current is not App app)
        {
            return;
        }

        var dialog = new MaterialChannelVideoDeleteWindow(ViewModel.SelectedProject.NewTitle);
        var accepted = await dialog.ShowDialog<bool>(OwnerWindow);
        if (!accepted)
        {
            return;
        }

        var service = app.Services.GetRequiredService<WeixinMaterialChannelVideoDeleteService>();
        try
        {
            ViewModel.StatusMessage = $"开始删除视频号素材：关键词“{dialog.Keyword}”，数量 {dialog.DeleteCount}";
            ViewModel.AppendExternalLog(ViewModel.StatusMessage, ViewModel.SelectedProject.ProjectKey, ViewModel.SelectedProject.DisplayName, "material-upload", "素材上传");

            var result = await service.DeleteAsync(
                ViewModel.SelectedProject.SourceProjectDir,
                ViewModel.SelectedProject.MaterialPublishConfigPath,
                dialog.Keyword,
                dialog.DeleteCount,
                new Progress<string>(message =>
                {
                    ViewModel.AppendExternalLog(message, ViewModel.SelectedProject.ProjectKey, ViewModel.SelectedProject.DisplayName, "material-upload", "素材上传");
                    ViewModel.StatusMessage = message;
                }),
                CancellationToken.None);

            var summary = $"视频号素材删除完成，共删除 {result.DeletedCount} 条。";
            ViewModel.AppendExternalLog(summary, ViewModel.SelectedProject.ProjectKey, ViewModel.SelectedProject.DisplayName, "material-upload", "素材上传");
            ViewModel.StatusMessage = summary;
        }
        catch (Exception ex)
        {
            ViewModel.AppendExternalLog(
                $"视频号素材删除失败：{ex.Message}",
                ViewModel.SelectedProject.ProjectKey,
                ViewModel.SelectedProject.DisplayName,
                "material-upload",
                "素材上传",
                isFailure: true);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async void OpenClipConfigButton_Click(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is null || Application.Current is not App app)
        {
            return;
        }

        var settingsService = app.Services.GetRequiredService<GlobalSettingsService>();
        var window = new MaterialClipConfigWindow(settingsService);
        await window.ShowDialog<bool>(OwnerWindow);
        ViewModel?.AppendExternalLog("已打开剪辑配置窗口。", stepKey: "material-upload", stepLabel: "素材上传");
    }

    // 纯 C# 高光剪辑引擎入口：选项目目录 → 火山 ASR + ffmpeg → 素材剪辑输出/高光/。
    private static readonly string[] ClipVideoExt = { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm" };

    private async void GenerateClipsFullEngineButton_Click(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is null || Application.Current is not App app)
        {
            return;
        }

        var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要生成剪辑的项目目录",
            AllowMultiple = false,
        });
        var projectDir = folders.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return;
        }

        // 源视频：<project>/videos 优先，否则项目目录顶层。
        var videos = EnumerateVideos(Path.Combine(projectDir, "videos"));
        if (videos.Count == 0) videos = EnumerateVideos(projectDir);
        if (videos.Count == 0)
        {
            ViewModel?.AppendExternalLog($"未在 {projectDir} 找到可剪辑的源视频。",
                stepKey: "material-upload", stepLabel: "素材上传", isFailure: true);
            return;
        }

        var clip = ChannelsPublisher.Core.Config.ClipConfig.Load();
        var settings = app.Services.GetRequiredService<GlobalSettingsService>().Load();
        var opts = BuildClipEngineOptions(clip, settings);
        if (string.IsNullOrWhiteSpace(opts.VolcAppId))
        {
            ViewModel?.AppendExternalLog("未配置火山 ASR 凭据（系统服务 → ASR配置），无法生成高光剪辑。",
                stepKey: "material-upload", stepLabel: "素材上传", isFailure: true);
            return;
        }

        var episodes = videos.Select((v, i) => new EpisodeSource(i + 1, v)).ToList();
        var engine = new ClipEngine();

        GenerateClipsFullEngineButton.IsEnabled = false;
        ViewModel?.AppendExternalLog(
            $"纯 C# 高光引擎：{Path.GetFileName(projectDir)}（{videos.Count} 源视频）开始…（火山 ASR + ffmpeg，较慢）",
            stepKey: "material-upload", stepLabel: "素材上传");
        if (ViewModel != null) ViewModel.StatusMessage = "高光剪辑生成中…";
        try
        {
            var result = await Task.Run(() => engine.GenerateAsync(
                projectDir, episodes, opts,
                msg => Dispatcher.UIThread.Post(() =>
                    ViewModel?.AppendExternalLog(msg, stepKey: "material-upload", stepLabel: "素材上传")),
                CancellationToken.None));
            if (result.Ok)
            {
                ViewModel?.AppendExternalLog(
                    $"高光剪辑完成：{result.Outputs.Count} 条 → {Path.Combine(projectDir, "素材剪辑输出", "高光")}",
                    stepKey: "material-upload", stepLabel: "素材上传");
                if (ViewModel != null) ViewModel.StatusMessage = $"剪辑完成：{result.Outputs.Count} 条";
            }
            else
            {
                ViewModel?.AppendExternalLog($"高光剪辑失败：{result.Error}",
                    stepKey: "material-upload", stepLabel: "素材上传", isFailure: true);
                if (ViewModel != null) ViewModel.StatusMessage = "剪辑生成失败";
            }
        }
        catch (Exception ex)
        {
            ViewModel?.AppendExternalLog($"高光剪辑出错：{ex.Message}",
                stepKey: "material-upload", stepLabel: "素材上传", isFailure: true);
        }
        finally
        {
            GenerateClipsFullEngineButton.IsEnabled = true;
        }
    }

    private static List<string> EnumerateVideos(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p => ClipVideoExt.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ClipConfig(画质/条数/速度) + 用户 GlobalSettings(火山 ASR 凭据/语言) → 剪辑引擎选项。
    private static ClipEngineOptions BuildClipEngineOptions(
        ChannelsPublisher.Core.Config.ClipConfig clip,
        ShortDrama.Desktop.Models.GlobalConfigSnapshot settings)
    {
        var (w, h) = (clip.OutputQuality ?? "").Trim().ToUpperInvariant() == "720P" ? (720, 1280) : (1080, 1920);
        var hi = clip.Modes.FirstOrDefault(m => m.Key == "highlight");
        var modes = clip.Modes.Where(m => m.Enabled).Select(m => m.Key).ToList();
        if (modes.Count == 0) modes.Add("highlight");
        return new ClipEngineOptions
        {
            Width = w,
            Height = h,
            Modes = modes,
            ClipCount = Math.Max(1, hi?.Count ?? 3),
            RenderSpeed = string.IsNullOrWhiteSpace(clip.RenderSpeed) ? "fast" : clip.RenderSpeed,
            HardwareEncode = clip.HardwareEncode,
            AudioEnergy = clip.AudioEnergy,
            EnableLlmScore = clip.EnableLlmScore,
            AsrEngine = string.IsNullOrWhiteSpace(settings.MaterialClipAsrEngine) ? "volcengine" : settings.MaterialClipAsrEngine,
            VolcAppId = settings.MaterialClipVolcengineAppId,
            VolcAccessToken = settings.MaterialClipVolcengineAccessToken,
            AsrLanguage = string.IsNullOrWhiteSpace(settings.MaterialClipAsrLanguage) ? "zh-CN" : settings.MaterialClipAsrLanguage,
            LocalModelDir = settings.MaterialClipAsrLocalModelDir,
            LocalVadPath = settings.MaterialClipAsrLocalVadPath,
            LocalUseItn = settings.MaterialClipAsrLocalUseItn,
            HybridMinCharsPerSec = double.TryParse(settings.MaterialClipAsrHybridMinCharsPerSec, out var hybThr) ? hybThr : 1.0,
            AiEndpoint = settings.AiTextEndpoint,
            AiApiKey = settings.AiTextApiKey,
            AiModel = settings.AiTextModel,
            OrigEnabled = clip.OrigEnabled,
            OrigZoom = clip.OrigZoom,
            OrigColor = clip.OrigColor,
            OrigSpeed = clip.OrigSpeed,
            OrigFade = clip.OrigFade,
            OrigStickerDir = clip.OrigStickerDir,
        };
    }
}
