using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShortDrama.Desktop.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ShortDrama.Desktop.Views;

public partial class MaterialPublishConfigWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly string[] PublishVideoFilePatterns = ["*.mp4", "*.mov", "*.m4v", "*.avi", "*.mkv", "*.webm"];
    private static readonly HashSet<string> PublishVideoFileSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".avi",
        ".mkv",
        ".webm"
    };
    private const string DefaultDescriptionTemplate = "{剧集名称}，热播爆火剧，点击链接，免费观看全集。#热门#爆火 {标签}";
    private const int DefaultSystemHighlightCount = 10;

    private readonly string _configPath;
    private readonly string _projectDir;
    private JsonObject _root = new();
    private JsonObject _videoPublish = new();
    private string _originalVideoPublishJson = "{}";
    private bool _syncingEpisodePreview;

    public MaterialPublishConfigWindow(string configPath, string projectTitle, string? preferredSourceMode = null)
    {
        _configPath = configPath;
        _projectDir = Path.GetDirectoryName(configPath) ?? string.Empty;
        InitializeComponent();

        ProjectTitleTextBlock.Text = $"发表配置：{projectTitle}";
        ConfigPathTextBlock.Text = configPath;
        SetupOptions();
        LoadConfig();
        if (!string.IsNullOrWhiteSpace(preferredSourceMode))
        {
            SelectOption(VideoSourceModeComboBox, preferredSourceMode);
        }

        HookEvents();
        RefreshDynamicState();
    }

    private sealed record OptionItem(string Key, string Label);

    private void SetupOptions()
    {
        BindOptions(VideoSourceModeComboBox,
        [
            new("project", "项目默认视频"),
            new("custom_files", "自选视频文件"),
            new("material_clips", "素材剪辑输出视频"),
            new("new_drama_mount", "新剧挂载模式"),
            new("downloaded_system_highlight", "下载的系统高光视频"),
            new("material_video_download", "下载素材视频"),
            new("system_highlight", "系统生成高光视频"),
            new("project_materials", "项目素材 material-videos"),
            new("source_videos", "源视频"),
            new("directory_publish", "目录批量发表")
        ]);

        BindOptions(EpisodeSelectionModeComboBox,
        [
            new("all", "全集"),
            new("range", "连续区间"),
            new("explicit", "具体集数")
        ]);

        BindOptions(RunStrategyComboBox,
        [
            new("all", "全部按配置重跑"),
            new("resume", "断点续跑"),
            new("retry_failed", "只重试失败集")
        ]);

        BindOptions(FinalActionComboBox,
        [
            new("publish", "发表"),
            new("draft", "保存草稿"),
            new("none", "只填不发")
        ]);

        BindOptions(SingleTestActionComboBox,
        [
            new("draft", "保存草稿"),
            new("publish", "发表"),
            new("none", "只填不发")
        ]);
    }

    private static void BindOptions(ComboBox comboBox, IReadOnlyList<OptionItem> items)
    {
        comboBox.ItemsSource = items;
        comboBox.DisplayMemberBinding = new Binding(nameof(OptionItem.Label));
        if (items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private void HookEvents()
    {
        SaveButton.Click += SaveButton_Click;
        CancelButton.Click += (_, _) => Close(false);
        RevealButton.Click += (_, _) => RevealConfigPath();
        RestoreDefaultsButton.Click += (_, _) => RestoreDefaults();
        BrowseCustomVideoFilesButton.Click += BrowseCustomVideoFilesButton_Click;
        ClearCustomVideoFilesButton.Click += (_, _) =>
        {
            CustomVideoFilesTextBox.Clear();
            RefreshDynamicState();
        };
        BrowseCoverButton.Click += BrowseCoverButton_Click;
        ClearCoverButton.Click += (_, _) =>
        {
            CoverImagePathTextBox.Clear();
            RefreshCoverPathPreview();
        };

        VideoSourceModeComboBox.SelectionChanged += (_, _) => RefreshDynamicState();
        EpisodeSelectionModeComboBox.SelectionChanged += (_, _) => RefreshEpisodeInputMode();
        StartEpisodeUpDown.PropertyChanged += (_, e) =>
        {
            if (e.Property == NumericUpDown.ValueProperty)
            {
                RefreshEpisodeInputMode();
            }
        };
        PublishCountUpDown.PropertyChanged += (_, e) =>
        {
            if (e.Property == NumericUpDown.ValueProperty)
            {
                RefreshEpisodeInputMode();
            }
        };
        MergePublishCheckBox.IsCheckedChanged += (_, _) => RefreshEpisodeInputMode();
        CustomVideoFilesTextBox.TextChanged += (_, _) => RefreshDynamicState();
        DramaTitleTextBox.TextChanged += (_, _) => RefreshDynamicState();

        FillDescriptionCheckBox.IsCheckedChanged += (_, _) => RefreshDescriptionWidgets();
        AiDescriptionCheckBox.IsCheckedChanged += (_, _) => RefreshDescriptionWidgets();
        PrependHashCheckBox.IsCheckedChanged += (_, _) => RefreshDescriptionWidgets();
        DescriptionTemplateTextBox.TextChanged += (_, _) => RefreshDescriptionWidgets();
        ReplaceCoverCheckBox.IsCheckedChanged += (_, _) => RefreshCoverPathPreview();
        CoverImagePathTextBox.TextChanged += (_, _) => RefreshCoverPathPreview();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                _root = JsonNode.Parse(File.ReadAllText(_configPath, Encoding.UTF8)) as JsonObject ?? new JsonObject();
            }
        }
        catch
        {
            _root = new JsonObject();
        }

        _videoPublish = _root["video_publish"] as JsonObject ?? new JsonObject();
        if (_videoPublish.Parent is null)
        {
            _root["video_publish"] = _videoPublish;
        }

        _originalVideoPublishJson = _videoPublish.ToJsonString();
        LoadControlsFromConfig(_videoPublish);
    }

    private void LoadControlsFromConfig(JsonObject config)
    {
        EnabledCheckBox.IsChecked = ReadBool(config, "enabled", true);
        SelectOption(
            VideoSourceModeComboBox,
            ResolveDisplaySourceMode(
                ReadString(config, "publish_video_source_mode")
                ?? ReadString(config, "video_source_mode")
                ?? "project",
                ReadBool(config, "highlight_publish_enabled", false)));
        DramaTitleTextBox.Text = FirstString(
            config,
            "new_drama_mount_title",
            "system_highlight_drama_title",
            "system_highlight_title");
        SelectOption(EpisodeSelectionModeComboBox, ReadString(config, "episode_selection_mode") ?? InferEpisodeSelectionMode(config));
        StartEpisodeUpDown.Value = ReadInt(config, "start_episode_index", 2);
        PublishCountUpDown.Value = ReadInt(config, "publish_count", 4);
        EpisodeIndexesTextBox.Text = FormatIntArray(config, "episode_indexes");
        CustomVideoFilesTextBox.Text = string.Join(Environment.NewLine, ReadStringArray(config, "publish_video_custom_files"));

        SelectOption(RunStrategyComboBox, ReadString(config, "run_strategy") ?? "resume");
        SelectOption(FinalActionComboBox, ReadString(config, "final_action") ?? "draft");
        SelectOption(SingleTestActionComboBox, ReadString(config, "single_test_final_action") ?? "draft");
        PauseOnErrorCheckBox.IsChecked = ReadBool(config, "pause_on_error", ReadBool(_root, "pause_on_error", true));
        FastModeCheckBox.IsChecked = ReadBool(config, "fast_mode", false);
        AllowDuplicateCheckBox.IsChecked = ReadBool(config, "allow_duplicate_publish", false);
        CaptureScreenshotsCheckBox.IsChecked = ReadBool(config, "capture_flow_screenshots", false);
        CaptureDebugDumpsCheckBox.IsChecked = ReadBool(config, "capture_flow_debug_dumps", false);
        MergePublishCheckBox.IsChecked = ReadBool(config, "merge_publish_enabled", false);
        MergeGroupSizeUpDown.Value = ReadInt(config, "merge_publish_group_size", 0);

        FillDescriptionCheckBox.IsChecked = ReadBool(config, "fill_description", true);
        AiDescriptionCheckBox.IsChecked = ReadBool(config, "ai_description_enabled", false);
        AiUseDialogueCheckBox.IsChecked = ReadBool(config, "ai_description_use_asr", true);
        PrependHashCheckBox.IsChecked = ReadBool(config, "prepend_hash_to_description", true);
        DescriptionTemplateTextBox.Text = ReadString(config, "description_template") ?? DefaultDescriptionTemplate;
        LocationOptionTextBox.Text = ReadString(config, "location_option_text") ?? "不显示";
        ActivityOptionTextBox.Text = ReadString(config, "activity_option_text") ?? "不参与活动";
        LinkOptionTextBox.Text = ReadString(config, "link_option_text") ?? "视频号剧集";
        TimingOptionTextBox.Text = ReadString(config, "timing_option_text") ?? "不定时";
        LinkPickerButtonTextBox.Text = ReadString(config, "link_picker_button_text") ?? "选择需要添加的剧集";
        LinkDialogTitleTextBox.Text = ReadString(config, "link_dialog_title") ?? "选择需要关联的视频号剧集";
        LinkSearchPlaceholderTextBox.Text = ReadString(config, "link_search_placeholder") ?? "搜索内容";

        CoverImagePathTextBox.Text = ReadString(config, "cover_image_path") ?? string.Empty;
        ReplaceCoverCheckBox.IsChecked = ReadBool(config, "replace_cover_with_local_image", false);
        FillShortTitleCheckBox.IsChecked = ReadBool(config, "fill_short_title", false);
        ShortTitleMaxLengthUpDown.Value = ReadInt(config, "short_title_max_length", 15);
        DeclareOriginalCheckBox.IsChecked = ReadBool(config, "declare_original", false);
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            DesktopCrashTrace.Write("material-publish-config save click begin");
            if (!ValidateBeforeSave(out var message))
            {
                DesktopCrashTrace.Write("material-publish-config save validation failed");
                ValidationMessageTextBlock.Text = message;
                return;
            }

            DesktopCrashTrace.Write("material-publish-config save config begin");
            SaveConfig();
            DesktopCrashTrace.Write("material-publish-config save config complete");
            SaveButton.IsEnabled = false;
            Dispatcher.UIThread.Post(CloseAfterSave, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            DesktopCrashTrace.Write($"material-publish-config save exception: {ex}");
            SaveButton.IsEnabled = true;
            ValidationMessageTextBlock.Text = $"保存失败：{ex.Message}";
        }
    }

    private void CloseAfterSave()
    {
        try
        {
            DesktopCrashTrace.Write("material-publish-config close begin");
            Close(true);
            DesktopCrashTrace.Write("material-publish-config close requested");
        }
        catch (Exception ex)
        {
            DesktopCrashTrace.Write($"material-publish-config close exception: {ex}");
            SaveButton.IsEnabled = true;
            ValidationMessageTextBlock.Text = $"关闭配置窗口失败：{ex.Message}";
        }
    }

    private bool ValidateBeforeSave(out string message)
    {
        message = string.Empty;
        if (EnabledCheckBox.IsChecked != true)
        {
            return true;
        }

        if (IsManualCustomVideoFilesMode() && ParseCustomVideoFiles().Count == 0)
        {
            message = "请选择至少一个自选视频文件。";
            PublishConfigTabs.SelectedIndex = 0;
            CustomVideoFilesTextBox.Focus();
            return false;
        }

        if (IsNewDramaMountMode() && string.IsNullOrWhiteSpace(DramaTitleTextBox.Text))
        {
            message = "新剧挂载模式需要填写新剧名称。";
            PublishConfigTabs.SelectedIndex = 0;
            DramaTitleTextBox.Focus();
            return false;
        }

        if (!IsCustomSourceMode() &&
            CurrentEpisodeMode() != "all" &&
            ParseEpisodeIndexesForSave().Count == 0)
        {
            message = "请确认发表集数有效，例如 2,3,4,7。";
            PublishConfigTabs.SelectedIndex = 0;
            EpisodeIndexesTextBox.Focus();
            return false;
        }

        var coverPath = CoverImagePathTextBox.Text?.Trim();
        if (ReplaceCoverCheckBox.IsChecked == true &&
            !string.IsNullOrWhiteSpace(coverPath) &&
            !IsDownloadedSystemHighlightMode() &&
            !File.Exists(ResolveProjectPath(coverPath)))
        {
            message = "封面图片不存在。";
            PublishConfigTabs.SelectedIndex = 2;
            CoverImagePathTextBox.Focus();
            return false;
        }

        return true;
    }

    private void SaveConfig()
    {
        _root["task_type"] = "publish_videos";
        _root["pause_on_error"] = PauseOnErrorCheckBox.IsChecked == true;
        if (_videoPublish.Parent is null)
        {
            _root["video_publish"] = _videoPublish;
        }

        var sourceMode = CurrentSourceMode();
        _videoPublish["enabled"] = EnabledCheckBox.IsChecked == true;
        _videoPublish["publish_video_source_mode"] = sourceMode;
        _videoPublish["video_source_mode"] = sourceMode;
        _videoPublish["new_drama_mount_title"] = DramaTitleTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["system_highlight_drama_title"] = IsSystemHighlightMode()
            ? string.Empty
            : DramaTitleTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["episode_selection_mode"] = CurrentEpisodeMode();
        _videoPublish["start_episode_index"] = NumberValue(StartEpisodeUpDown, 2);
        _videoPublish["publish_count"] = NumberValue(PublishCountUpDown, 4);
        _videoPublish["episode_indexes"] = ToJsonArray(ParseEpisodeIndexesForSave());
        _videoPublish["publish_video_custom_files"] = ToJsonArray(ParseCustomVideoFiles());
        _videoPublish.Remove("highlight_publish_edit_mode");
        _videoPublish.Remove("material_transcode");

        _videoPublish["run_strategy"] = SelectedKey(RunStrategyComboBox, "resume");
        _videoPublish["final_action"] = SelectedKey(FinalActionComboBox, "draft");
        _videoPublish["single_test_final_action"] = SelectedKey(SingleTestActionComboBox, "draft");
        _videoPublish["pause_on_error"] = PauseOnErrorCheckBox.IsChecked == true;
        _videoPublish["fast_mode"] = FastModeCheckBox.IsChecked == true;
        _videoPublish["allow_duplicate_publish"] = AllowDuplicateCheckBox.IsChecked == true;
        _videoPublish["capture_flow_screenshots"] = CaptureScreenshotsCheckBox.IsChecked == true;
        _videoPublish["capture_flow_debug_dumps"] = CaptureDebugDumpsCheckBox.IsChecked == true;
        _videoPublish["merge_publish_enabled"] = MergePublishCheckBox.IsChecked == true;
        _videoPublish["merge_publish_group_size"] = NumberValue(MergeGroupSizeUpDown, 0);

        _videoPublish["fill_description"] = FillDescriptionCheckBox.IsChecked == true;
        _videoPublish["ai_description_enabled"] = AiDescriptionCheckBox.IsChecked == true;
        _videoPublish["ai_description_use_asr"] = AiUseDialogueCheckBox.IsChecked == true;
        _videoPublish["prepend_hash_to_description"] = PrependHashCheckBox.IsChecked == true;
        _videoPublish["description_template"] = EnsureDescriptionTemplateHasTags(DescriptionTemplateTextBox.Text);
        _videoPublish["location_option_text"] = LocationOptionTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["activity_option_text"] = ActivityOptionTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["link_option_text"] = LinkOptionTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["timing_option_text"] = TimingOptionTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["link_picker_button_text"] = LinkPickerButtonTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["link_dialog_title"] = LinkDialogTitleTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["link_search_placeholder"] = LinkSearchPlaceholderTextBox.Text?.Trim() ?? string.Empty;

        var coverPath = CoverImagePathTextBox.Text?.Trim() ?? string.Empty;
        _videoPublish["cover_image_path"] = ReplaceCoverCheckBox.IsChecked == true
            ? NormalizePathForSave(coverPath)
            : string.Empty;
        _videoPublish["replace_cover_with_local_image"] = ReplaceCoverCheckBox.IsChecked == true;
        _videoPublish["fill_short_title"] = FillShortTitleCheckBox.IsChecked == true;
        _videoPublish["short_title_max_length"] = NumberValue(ShortTitleMaxLengthUpDown, 15);
        _videoPublish["declare_original"] = DeclareOriginalCheckBox.IsChecked == true;

        var uploadAction = _videoPublish["video_upload_action"] as JsonObject ?? new JsonObject();
        uploadAction["input_selector"] = ReadString(uploadAction, "input_selector") ?? "input[type='file'][accept*='video'], input[type='file']";
        if (uploadAction.Parent is null)
        {
            _videoPublish["video_upload_action"] = uploadAction;
        }

        RemoveRuntimeFlags(_videoPublish);
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? ".");
        File.WriteAllText(_configPath, _root.ToJsonString(JsonOptions), Encoding.UTF8);
    }

    private void RefreshDynamicState()
    {
        RefreshVideoSourceMode();
        RefreshDescriptionWidgets();
        RefreshCoverPathPreview();
        ValidationMessageTextBlock.Text = string.Empty;
    }

    private void RefreshVideoSourceMode()
    {
        var manualCustomMode = IsManualCustomVideoFilesMode();
        CustomVideoFilesTextBox.IsEnabled = manualCustomMode;
        BrowseCustomVideoFilesButton.IsEnabled = manualCustomMode;
        ClearCustomVideoFilesButton.IsEnabled = manualCustomMode;

        var titleVisible = IsNewDramaMountMode();
        DramaTitleLabelTextBlock.IsVisible = titleVisible;
        DramaTitleTextBox.IsVisible = titleVisible;
        DramaTitleHintTextBlock.IsVisible = titleVisible || IsSystemHighlightMode();

        if (IsDownloadedSystemHighlightMode())
        {
            CustomVideoFilesHintTextBlock.Text = "当前使用下载的系统高光视频；会自动读取项目目录下的下载成片，无需手动选择文件。";
            DramaTitleHintTextBlock.Text = string.Empty;
        }
        else if (IsManualCustomVideoFilesMode())
        {
            CustomVideoFilesHintTextBlock.Text = $"已选择 {ParseCustomVideoFiles().Count} 个有效视频。运行时将逐个发表这些文件。";
            DramaTitleHintTextBlock.Text = string.Empty;
        }
        else if (IsMaterialClipsMode())
        {
            CustomVideoFilesHintTextBlock.Text = "当前使用素材剪辑输出视频；会读取当前项目 workflow/素材剪辑输出 下的可上传成片。";
            DramaTitleHintTextBlock.Text = string.Empty;
        }
        else if (IsSystemHighlightMode())
        {
            CustomVideoFilesHintTextBlock.Text = "当前使用系统生成高光视频；集数配置在该模式下表示高光编号。";
            DramaTitleHintTextBlock.Text = "该来源无需填写新剧名称，会使用当前项目的新剧名搜索剧集详情。";
        }
        else if (IsNewDramaMountMode())
        {
            CustomVideoFilesHintTextBlock.Text = "当前使用新剧挂载模式；自选视频文件列表不会参与本次发表。";
            DramaTitleHintTextBlock.Text = "输入新剧名称，运行时自动搜索并下载对应素材。";
        }
        else
        {
            CustomVideoFilesHintTextBlock.Text = "当前使用项目默认视频；自选视频文件列表不会参与本次发表。";
            DramaTitleHintTextBlock.Text = string.Empty;
        }

        RefreshEpisodeInputMode();
    }

    private void RefreshEpisodeInputMode()
    {
        if (_syncingEpisodePreview)
        {
            return;
        }

        var customMode = IsCustomSourceMode();
        var allMode = CurrentEpisodeMode() == "all";
        var explicitMode = CurrentEpisodeMode() == "explicit";

        EpisodeSelectionModeComboBox.IsEnabled = !customMode;
        StartEpisodeUpDown.IsEnabled = !customMode && !allMode && !explicitMode;
        PublishCountUpDown.IsEnabled = !customMode && !allMode && !explicitMode;
        EpisodeIndexesTextBox.IsEnabled = !customMode;
        EpisodeIndexesTextBox.IsReadOnly = !explicitMode;
        MergeGroupSizeUpDown.IsEnabled = MergePublishCheckBox.IsChecked == true;

        if (customMode)
        {
            EpisodeIndexesLabelTextBlock.Text = "实际发表视频";
            EpisodeIndexesHintTextBlock.Text = $"自选/下载视频来源不使用集数配置；当前有效视频 {ParseCustomVideoFiles().Count} 个。";
            return;
        }

        if (allMode)
        {
            EpisodeIndexesLabelTextBlock.Text = "实际发表集数";
            if (IsSystemHighlightMode())
            {
                SetEpisodeIndexesPreview(Enumerable.Range(1, DefaultSystemHighlightCount));
                EpisodeIndexesHintTextBlock.Text = $"当前为全集模式，默认发表 {DefaultSystemHighlightCount} 个系统高光。";
            }
            else
            {
                EpisodeIndexesHintTextBlock.Text = IsMaterialClipsMode()
                    ? "当前为全集模式，运行时会发表素材剪辑输出里的全部可上传成片。"
                    : "当前为全集模式，运行时会发表当前来源中的全部集数。";
            }
            return;
        }

        if (explicitMode)
        {
            EpisodeIndexesLabelTextBlock.Text = "具体集数";
            EpisodeIndexesHintTextBlock.Text = IsSystemHighlightMode()
                ? "例如：1,3,5。这里填写要发表的高光编号。"
                : "例如：2,3,4,7。只会发表这里列出的集数。";
            return;
        }

        EpisodeIndexesLabelTextBlock.Text = "实际发表集数（预览）";
        EpisodeIndexesHintTextBlock.Text = IsSystemHighlightMode()
            ? "连续区间模式下，这里预览将要发表的高光编号，不能单独保存。"
            : "连续区间模式下，这里是自动生成的预览，不能单独保存。";
        SetEpisodeIndexesPreview(ParseRangeEpisodeIndexes());
    }

    private void RefreshDescriptionWidgets()
    {
        var enabled = FillDescriptionCheckBox.IsChecked == true;
        var aiEnabled = enabled && AiDescriptionCheckBox.IsChecked == true;
        DescriptionTemplateTextBox.IsEnabled = enabled;
        AiDescriptionCheckBox.IsEnabled = enabled;
        AiUseDialogueCheckBox.IsVisible = IsNewDramaMountMode();
        AiUseDialogueCheckBox.IsEnabled = aiEnabled && IsNewDramaMountMode();
        PrependHashCheckBox.IsEnabled = enabled;

        DescriptionPreviewTextBlock.Text = enabled
            ? BuildDescriptionPreview()
            : "当前未启用视频描述填写。";
    }

    private string BuildDescriptionPreview()
    {
        var template = EnsureDescriptionTemplateHasTags(DescriptionTemplateTextBox.Text);
        var preview = template
            .Replace("{剧集名称}", "示例新剧名")
            .Replace("{新剧名}", "示例新剧名")
            .Replace("{原剧名}", "示例原剧名")
            .Replace("{短标题}", "示例短标题")
            .Replace("{标签}", "#示例标签1#示例标签2")
            .Replace("{视频文件名}", "示例第2集.mp4")
            .Trim();

        if (PrependHashCheckBox.IsChecked == true && preview.Length > 0 && !preview.StartsWith('#'))
        {
            preview = "#" + preview;
        }

        return preview.Length > 60 ? preview[..60] + "..." : preview.Length == 0 ? "空" : preview;
    }

    private void RefreshCoverPathPreview()
    {
        var enabled = ReplaceCoverCheckBox.IsChecked == true;
        CoverImagePathTextBox.IsEnabled = enabled;
        BrowseCoverButton.IsEnabled = enabled;
        ClearCoverButton.IsEnabled = enabled;
        if (!enabled)
        {
            CoverPathPreviewTextBlock.Text = "当前未启用本地封面替换。";
            return;
        }

        var rawPath = CoverImagePathTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            CoverPathPreviewTextBlock.Text = IsDownloadedSystemHighlightMode()
                ? "当前使用下载的系统高光视频；会优先使用每条视频旁的 .cover.jpg 封面。"
                : "当前未指定封面图片，将自动尝试使用项目海报图。";
            return;
        }

        var resolved = ResolveProjectPath(rawPath);
        CoverPathPreviewTextBlock.Text = File.Exists(resolved)
            ? $"当前封面路径：{resolved}"
            : $"当前封面路径不存在：{resolved}";
    }

    private void RestoreDefaults()
    {
        _videoPublish = JsonNode.Parse(_originalVideoPublishJson) as JsonObject ?? new JsonObject();
        LoadControlsFromConfig(_videoPublish);
        RefreshDynamicState();
    }

    private async void BrowseCustomVideoFilesButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频文件",
            AllowMultiple = true,
            SuggestedStartLocation = await SuggestedFolderAsync(ResolveCustomVideoStartDirectory()),
            FileTypeFilter =
            [
                new FilePickerFileType("视频文件")
                {
                    Patterns = PublishVideoFilePatterns
                },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var lines = ReadTextLines(CustomVideoFilesTextBox.Text).ToList();
        lines.AddRange(files.Select(file => NormalizePathForSave(file.Path.LocalPath)));
        CustomVideoFilesTextBox.Text = string.Join(Environment.NewLine, lines.Distinct(StringComparer.OrdinalIgnoreCase));
        RefreshDynamicState();
    }

    private async void BrowseCoverButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择封面图片",
            AllowMultiple = false,
            SuggestedStartLocation = await SuggestedFolderAsync(ResolveCoverStartDirectory()),
            FileTypeFilter =
            [
                new FilePickerFileType("图片文件")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"]
                },
                FilePickerFileTypes.All
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        CoverImagePathTextBox.Text = NormalizePathForSave(file.Path.LocalPath);
        RefreshCoverPathPreview();
    }

    private async Task<IStorageFolder?> SuggestedFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        return await StorageProvider.TryGetFolderFromPathAsync(path);
    }

    private string ResolveCustomVideoStartDirectory()
    {
        var firstExisting = ParseCustomVideoFiles()
            .Select(ResolveProjectPath)
            .FirstOrDefault(File.Exists);
        return firstExisting is not null ? Path.GetDirectoryName(firstExisting) ?? _projectDir : _projectDir;
    }

    private string ResolveCoverStartDirectory()
    {
        var coverPath = CoverImagePathTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(coverPath))
        {
            var resolved = ResolveProjectPath(coverPath);
            if (File.Exists(resolved))
            {
                return Path.GetDirectoryName(resolved) ?? _projectDir;
            }
        }

        return _projectDir;
    }

    private IReadOnlyList<string> ParseCustomVideoFiles()
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadTextLines(CustomVideoFilesTextBox.Text))
        {
            var resolved = ResolveProjectPath(line);
            if (!PublishVideoFileSuffixes.Contains(Path.GetExtension(resolved)))
            {
                continue;
            }

            var normalized = NormalizePathForSave(resolved);
            if (seen.Add(normalized))
            {
                paths.Add(normalized);
            }
        }

        return paths;
    }

    private IReadOnlyList<int> ParseEpisodeIndexesForSave()
    {
        return CurrentEpisodeMode() switch
        {
            "all" => [],
            "explicit" => ParseIndexes(EpisodeIndexesTextBox.Text),
            _ => ParseRangeEpisodeIndexes()
        };
    }

    private IReadOnlyList<int> ParseRangeEpisodeIndexes()
    {
        var startEpisode = Math.Max(1, NumberValue(StartEpisodeUpDown, 1));
        var publishCount = Math.Max(1, NumberValue(PublishCountUpDown, 1));
        return Enumerable.Range(startEpisode, publishCount).ToArray();
    }

    private void SetEpisodeIndexesPreview(IEnumerable<int> indexes)
    {
        _syncingEpisodePreview = true;
        try
        {
            EpisodeIndexesTextBox.Text = string.Join(",", indexes);
        }
        finally
        {
            _syncingEpisodePreview = false;
        }
    }

    private string CurrentSourceMode() => SelectedKey(VideoSourceModeComboBox, "project");

    private string CurrentEpisodeMode() => SelectedKey(EpisodeSelectionModeComboBox, "all");

    private bool IsCustomSourceMode() => IsManualCustomVideoFilesMode() || IsDownloadedSystemHighlightMode();

    private bool IsManualCustomVideoFilesMode() => CurrentSourceMode() == "custom_files";

    private bool IsDownloadedSystemHighlightMode() => CurrentSourceMode() == "downloaded_system_highlight";

    private bool IsMaterialClipsMode() => CurrentSourceMode() == "material_clips";

    private bool IsNewDramaMountMode() => CurrentSourceMode() == "new_drama_mount";

    private bool IsSystemHighlightMode() => CurrentSourceMode() == "system_highlight";

    private static string ResolveDisplaySourceMode(string sourceMode, bool highlightPublishEnabled)
    {
        if (highlightPublishEnabled && string.Equals(sourceMode, "project", StringComparison.OrdinalIgnoreCase))
        {
            return "material_clips";
        }

        return sourceMode;
    }

    private static string InferEpisodeSelectionMode(JsonObject config)
    {
        var mode = ReadString(config, "episode_selection_mode")?.Trim().ToLowerInvariant();
        if (mode is "all" or "range" or "explicit")
        {
            return mode;
        }

        return config.ContainsKey("start_episode_index") || config.ContainsKey("publish_count")
            ? "range"
            : "all";
    }

    private string ResolveProjectPath(string rawPath)
    {
        var text = rawPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            var expanded = text.StartsWith("~", StringComparison.Ordinal)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    text.TrimStart('~', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : text;
            return Path.IsPathFullyQualified(expanded) || string.IsNullOrWhiteSpace(_projectDir)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(_projectDir, expanded));
        }
        catch
        {
            return text;
        }
    }

    private string NormalizePathForSave(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var resolved = ResolveProjectPath(rawPath);
        if (string.IsNullOrWhiteSpace(_projectDir))
        {
            return resolved;
        }

        try
        {
            var relative = Path.GetRelativePath(_projectDir, resolved);
            return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative)
                ? relative
                : resolved;
        }
        catch
        {
            return resolved;
        }
    }

    private void RevealConfigPath()
    {
        try
        {
            var path = File.Exists(_configPath) ? _configPath : Path.GetDirectoryName(_configPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var arguments = File.Exists(_configPath)
                ? $"/select,\"{_configPath}\""
                : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static void RemoveRuntimeFlags(JsonObject videoPublish)
    {
        foreach (var key in videoPublish
                     .Select(property => property.Key)
                     .Where(key => key.StartsWith("_runtime_", StringComparison.Ordinal))
                     .ToArray())
        {
            videoPublish.Remove(key);
        }
    }

    private static string SelectedKey(ComboBox comboBox, string fallback) =>
        comboBox.SelectedItem is OptionItem item ? item.Key : fallback;

    private static void SelectOption(ComboBox comboBox, string key)
    {
        var items = comboBox.ItemsSource as IEnumerable<OptionItem>;
        var selected = items?.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            comboBox.SelectedItem = selected;
        }
    }

    private static int NumberValue(NumericUpDown control, int fallback)
    {
        try
        {
            return Convert.ToInt32(control.Value ?? fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private static string? ReadString(JsonObject root, string key)
    {
        return root[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static string? FirstString(JsonObject root, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ReadString(root, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool ReadBool(JsonObject root, string key, bool fallback)
    {
        if (root[key] is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean))
            {
                return boolean;
            }
        }

        return fallback;
    }

    private static int ReadInt(JsonObject root, string key, int fallback)
    {
        if (root[key] is JsonValue value)
        {
            if (value.TryGetValue<int>(out var integer))
            {
                return integer;
            }

            if (value.TryGetValue<string>(out var text) && int.TryParse(text, out integer))
            {
                return integer;
            }
        }

        return fallback;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject root, string key)
    {
        if (root[key] is JsonArray array)
        {
            return array
                .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
        }

        var textValue = ReadString(root, key);
        return ReadTextLines(textValue);
    }

    private static string FormatIntArray(JsonObject root, string key)
    {
        if (root[key] is not JsonArray array)
        {
            return string.Empty;
        }

        return string.Join(",", array.Select(item =>
        {
            if (item is JsonValue value)
            {
                if (value.TryGetValue<int>(out var integer))
                {
                    return integer.ToString();
                }

                if (value.TryGetValue<string>(out var text))
                {
                    return text.Trim();
                }
            }

            return string.Empty;
        }).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static IReadOnlyList<int> ParseIndexes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Replace('，', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, out var value) ? value : 0)
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<string> ReadTextLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Replace(';', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim().Trim('"'))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static JsonArray ToJsonArray(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string EnsureDescriptionTemplateHasTags(string? template)
    {
        var text = (template ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultDescriptionTemplate;
        }

        return text.Contains("{标签}", StringComparison.Ordinal) ? text : $"{text} {{标签}}";
    }
}
