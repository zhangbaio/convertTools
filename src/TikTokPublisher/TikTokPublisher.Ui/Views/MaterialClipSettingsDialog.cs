using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ChannelsPublisher.Clip;
using ChannelsPublisher.Core.Config;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

internal sealed class MaterialClipSettingsDialog : Window
{
    private static readonly string[] Modes = ["highlight", "mashup", "commentary", "slice"];
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Action> _apply = [];
    private readonly Dictionary<string, ModeEditor> _editors = new();
    private MaterialClipSettings _settings;

    public MaterialClipSettingsDialog(MaterialClipSettings settings, string error)
    {
        _settings = settings;
        Title = "全局剪辑配置";
        Width = 850;
        Height = 710;
        MinWidth = 590;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 10 };
        layout.Children.Add(new TextBlock
        {
            Text = "本机全局配置。保存后对新启动的剪辑任务生效。",
            Classes = { "pageSummary" },
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetRow(_tabs, 1);
        layout.Children.Add(_tabs);
        Grid.SetRow(_status, 2);
        _status.Text = error;
        layout.Children.Add(_status);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        var import = new Button { Content = "导入旧工具配置" };
        import.Click += async (_, _) => await ImportAsync(import);
        actions.Children.Add(import);
        var save = new Button { Content = "保存", Classes = { "primary" } };
        save.Click += (_, _) => Save();
        Grid.SetColumn(save, 2);
        actions.Children.Add(save);
        var close = new Button { Content = "关闭", IsCancel = true };
        close.Click += (_, _) => Close(false);
        Grid.SetColumn(close, 3);
        actions.Children.Add(close);
        Grid.SetRow(actions, 3);
        layout.Children.Add(actions);
        Content = layout;
        BuildTabs();
    }

    private void BuildTabs()
    {
        _apply.Clear();
        _editors.Clear();
        var clip = _settings.Clip;
        var tabs = new List<TabItem> { Page("基础", Basic(clip)) };
        foreach (var key in Modes)
        {
            var editor = new ModeEditor(clip, key);
            _editors.Add(key, editor);
            tabs.Add(Page(clip.Mode(key).Label, ModeBody(editor, key)));
        }
        tabs.Add(Page("原创度", Originality(clip)));
        tabs.Add(Page("服务配置", Services(_settings.Services)));
        _tabs.ItemsSource = tabs;
        _tabs.SelectedIndex = 0;
    }

    private StackPanel Basic(ClipConfig clip)
    {
        var panel = Fields();
        Section(panel, "选段和成片");
        Flag(panel, "按集时长分配高光选段名额", clip.EpisodeQuota, value => clip.EpisodeQuota = value);
        Flag(panel, "AI 复评分", clip.EnableLlmScore, value => clip.EnableLlmScore = value);
        Flag(panel, "音频能量加权", clip.AudioEnergy, value => clip.AudioEnergy = value);
        Flag(panel, "镜头密度加权", clip.ShotDensity, value => clip.ShotDensity = value);
        Flag(panel, "AI 叙事重排", clip.LlmArrange, value => clip.LlmArrange = value);
        Flag(panel, "片段去碎", clip.SmoothSelection, value => clip.SmoothSelection = value);
        Number(panel, "最短片段（毫秒）", clip.MinimumSegmentMs, 100, 10000, 100, value => clip.MinimumSegmentMs = (int)value);
        Flag(panel, "生成发表文案（.publish.json）", clip.PublishMeta, value => clip.PublishMeta = value);
        Flag(panel, "叠加开头剧情标题", clip.TitleCard, value => clip.TitleCard = value);
        Number(panel, "标题显示（秒）", clip.TitleCardSeconds, 1, 15, 1, value => clip.TitleCardSeconds = (int)value);
        Section(panel, "并发和导出");
        Number(panel, "并发集数", clip.PipelineConcurrent, 1, 16, 1, value => clip.PipelineConcurrent = (int)value);
        Number(panel, "网络并发（0 为自动）", clip.NetConcurrent, 0, 32, 1, value => clip.NetConcurrent = (int)value);
        Choice(panel, "导出清晰度", clip.OutputQuality, value => clip.OutputQuality = value,
            ("720p", "720P（自动适配横竖屏）"), ("1080p", "1080P（自动适配横竖屏）"));
        Choice(panel, "编码模式", clip.EncodeMode, value => clip.EncodeMode = value, ("bitrate", "固定码率"), ("quality", "质量优先"));
        Number(panel, "固定码率（Mbps）", clip.VideoBitrate, 0.8, 12, 0.1, value => clip.VideoBitrate = value);
        Choice(panel, "视频编码", clip.VideoCodec, value => clip.VideoCodec = value, ("h264", "H.264"), ("h265", "H.265 / HEVC"));
        Choice(panel, "渲染速度", clip.RenderSpeed, value => clip.RenderSpeed = value,
            ("fast", "快速"), ("balanced", "均衡"), ("quality", "质量优先"));
        Flag(panel, "优先硬件编码（失败时使用软件编码）", clip.HardwareEncode, value => clip.HardwareEncode = value);
        return panel;
    }

    private StackPanel ModeBody(ModeEditor editor, string key)
    {
        var panel = editor.Body;
        var clip = _settings.Clip;
        if (key == "highlight")
        {
            Flag(panel, "剧情高光（保护对白切点，配置 AI 后支持跨集选段）", clip.StoryHighlights, value => clip.StoryHighlights = value);
            Flag(panel, "本地 OCR 补充硬字幕（剧情高光）", clip.StoryHardSubtitles, value => clip.StoryHardSubtitles = value);
            Flag(panel, "画面复评（发现少对白和无对白的高光）", clip.StoryVisualReview, value => clip.StoryVisualReview = value);
        }
        if (key != "commentary")
            return panel;
        Section(panel, "旁白和字幕");
        Flag(panel, "定位并遮盖原字幕，烧录解说字幕", clip.CommentaryBurnSubtitles, value => clip.CommentaryBurnSubtitles = value);
        Number(panel, "定位失败时的底部遮盖高度（%）", clip.CommentarySubtitleBlurHeight * 100, 5, 35, 1,
            value => clip.CommentarySubtitleBlurHeight = value / 100);
        Number(panel, "旁白比例（%）", clip.CommentaryNarrationRatio, 40, 100, 1, value => clip.CommentaryNarrationRatio = value);
        Choice(panel, "解说风格", clip.CommentaryStyleStrength, value => clip.CommentaryStyleStrength = value,
            ("subtle", "克制"), ("standard", "标准"), ("strong", "强烈"));
        Choice(panel, "配音引擎", clip.TtsEngine, value => clip.TtsEngine = value, ("volcengine", "火山 TTS"), ("edge", "Edge TTS"));
        Text(panel, "火山音色", clip.TtsVoiceType, value => clip.TtsVoiceType = value, "留空使用 BV701_streaming");
        Text(panel, "火山 TTS 集群", clip.TtsCluster, value => clip.TtsCluster = value, "留空使用 volcano_tts");
        Text(panel, "Edge 音色", clip.TtsEdgeVoice, value => clip.TtsEdgeVoice = value, "zh-CN-YunjianNeural");
        Number(panel, "配音语速", clip.TtsSpeedRatio, 0.5, 2, 0.05, value => clip.TtsSpeedRatio = value);
        var preview = new Button { Content = "试听解说" };
        preview.Click += async (_, _) => await CheckAsync(preview, PreviewAsync);
        panel.Children.Add(preview);
        return panel;
    }

    private StackPanel Originality(ClipConfig clip)
    {
        var panel = Fields();
        Flag(panel, "启用原创度处理", clip.OrigEnabled, value => clip.OrigEnabled = value);
        Flag(panel, "微缩放裁剪", clip.OrigZoom, value => clip.OrigZoom = value);
        Flag(panel, "调色微调", clip.OrigColor, value => clip.OrigColor = value);
        Flag(panel, "轻微变速", clip.OrigSpeed, value => clip.OrigSpeed = value);
        Flag(panel, "开闭幕淡入淡出", clip.OrigFade, value => clip.OrigFade = value);
        Flag(panel, "前景放大增强", clip.OrigForegroundZoom, value => clip.OrigForegroundZoom = value);
        Flag(panel, "背景虚化边框", clip.OrigBlurBackground, value => clip.OrigBlurBackground = value);
        Flag(panel, "贴纸条带", clip.OrigStickerStrip, value => clip.OrigStickerStrip = value);
        Flag(panel, "轻颗粒噪声", clip.OrigNoise, value => clip.OrigNoise = value);
        Text(panel, "贴纸目录", clip.OrigStickerDir, value => clip.OrigStickerDir = value,
            "留空关闭贴纸，可选内置贴纸 1 / 2 或选择文件夹", folder: true, builtInStickers: true);
        return panel;
    }

    private StackPanel Services(MaterialClipConnections services)
    {
        var panel = Fields();
        Section(panel, "AI 文本（留空时使用系统设置）");
        Text(panel, "API 地址", services.AiEndpoint, value => services.AiEndpoint = value);
        Text(panel, "API Key", services.AiApiKey, value => services.AiApiKey = value, secret: true);
        Text(panel, "模型", services.AiModel, value => services.AiModel = value);
        Section(panel, "剧情高光画面复评（留空时复用 AI 文本服务）");
        Text(panel, "视觉 API 地址", services.StoryVisionEndpoint, value => services.StoryVisionEndpoint = value);
        Text(panel, "视觉 API Key", services.StoryVisionApiKey, value => services.StoryVisionApiKey = value, secret: true);
        Text(panel, "视觉模型", services.StoryVisionModel, value => services.StoryVisionModel = value, "例如 Qwen3-VL 或支持图片输入的豆包模型");
        Section(panel, "字幕识别 ASR");
        Choice(panel, "ASR 引擎", services.AsrEngine, value => services.AsrEngine = value,
            ("volcengine", "在线"), ("local", "本地"), ("hybrid", "混合（本地优先）"));
        Choice(panel, "在线识别服务", services.AsrProvider, value => services.AsrProvider = value,
            ("volcengine_stt", "火山 STT"), ("doubao_subtitle", "豆包字幕"));
        Text(panel, "识别语言", services.AsrLanguage, value => services.AsrLanguage = value, "例如 zh-CN、en、ja、ko");
        Text(panel, "火山 ASR APP ID", services.VolcAppId, value => services.VolcAppId = value, "可复用系统设置");
        Text(panel, "火山 ASR Token", services.VolcAccessToken, value => services.VolcAccessToken = value, secret: true);
        Text(panel, "豆包字幕 APP ID", services.DoubaoAppId, value => services.DoubaoAppId = value);
        Text(panel, "豆包字幕 Token", services.DoubaoAccessToken, value => services.DoubaoAccessToken = value, secret: true);
        Choice(panel, "本地 ASR 模型", services.LocalModel, value => services.LocalModel = value,
            ("sensevoice", "SenseVoice"), ("paraformer", "Paraformer"));
        Text(panel, "本地模型目录", services.LocalModelDir, value => services.LocalModelDir = value, folder: true);
        Text(panel, "VAD 模型文件", services.LocalVadPath, value => services.LocalVadPath = value, file: true);
        Flag(panel, "SenseVoice 数字逆归一化（ITN）", services.LocalUseItn, value => services.LocalUseItn = value);
        Number(panel, "混合引擎复核阈值（字/秒）", services.HybridMinCharsPerSec, 0, 20, 0.1,
            value => services.HybridMinCharsPerSec = value);
        Section(panel, "解说配音");
        Text(panel, "火山 TTS APP ID", services.TtsAppId, value => services.TtsAppId = value, "留空复用火山 ASR");
        Text(panel, "火山 TTS Token", services.TtsAccessToken, value => services.TtsAccessToken = value, secret: true);
        Text(panel, "Edge TTS 程序", services.EdgeTtsExecutable, value => services.EdgeTtsExecutable = value,
            "留空时从 PATH 查找 edge-tts.exe", file: true);
        Section(panel, "视频工具");
        Text(panel, "FFmpeg", services.FfmpegPath, value => services.FfmpegPath = value, "留空时自动查找", file: true);
        Text(panel, "FFprobe", services.FfprobePath, value => services.FfprobePath = value, "留空时自动查找", file: true);
        var test = new Button { Content = "检查 FFmpeg 和 FFprobe" };
        test.Click += async (_, _) => await CheckAsync(test, VerifyToolsAsync);
        panel.Children.Add(test);
        return panel;
    }

    private void Save()
    {
        try
        {
            foreach (var editor in _editors.Values)
                editor.Validate();
            foreach (var editor in _editors.Values)
                editor.Apply();
            ApplyFields();
            MaterialClipSettingsStore.Save(_settings);
            Close(true);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private void ApplyFields()
    {
        foreach (var binding in _apply)
            binding();
    }

    private async Task ImportAsync(Button button)
    {
        button.IsEnabled = false;
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择旧工具的 settings.json",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(MaterialClipSettingsStore.LegacyPath)!),
                FileTypeFilter = [new FilePickerFileType("旧工具配置") { Patterns = ["*.json"] }],
            });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path)
                return;
            var imported = MaterialClipSettingsStore.ImportLegacy(path, ClientSettingsStore.Load());
            ApplyFields();
            imported.Services.EdgeTtsExecutable = _settings.Services.EdgeTtsExecutable;
            imported.Services.FfmpegPath = _settings.Services.FfmpegPath;
            imported.Services.FfprobePath = _settings.Services.FfprobePath;
            _settings = imported;
            BuildTabs();
            _status.Text = "已读入旧工具剪辑配置，保存后生效。";
        }
        catch (Exception ex) { _status.Text = $"导入失败。{ex.Message}"; }
        finally { button.IsEnabled = true; }
    }

    private async Task CheckAsync(Button button, Func<ClipEngineOptions, Task<string>> check)
    {
        button.IsEnabled = false;
        _status.Text = "正在检查…";
        try
        {
            ApplyFields();
            var options = MaterialClipSettingsStore.BuildOptions(_settings, ClientSettingsStore.Load());
            _status.Text = await check(options);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { button.IsEnabled = true; }
    }

    private static async Task<string> VerifyToolsAsync(ClipEngineOptions options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await Ffmpeg.RunAsync(options.FfmpegPath, ["-version"], timeout.Token);
        await Ffmpeg.RunAsync(options.FfprobePath, ["-version"], timeout.Token);
        return "FFmpeg 和 FFprobe 均可用。";
    }

    private static async Task<string> PreviewAsync(ClipEngineOptions options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var audio = await ClipSpeech.SynthesizeAsync("这是一段素材剪辑的解说试听。", options.TtsSpeedRatio, options, timeout.Token);
        var path = Path.Combine(Path.GetTempPath(), $"clip-voice-preview-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, audio, timeout.Token);
        _ = DeletePreviewAsync(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        return "解说试听已在系统播放器打开。";
    }

    private static async Task DeletePreviewAsync(string path)
    {
        await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static TabItem Page(string label, Control body) => new()
    {
        Header = label,
        Content = new ScrollViewer { Content = body, Padding = new Thickness(4, 12, 12, 14) },
    };

    private static StackPanel Fields() => new() { Spacing = 8 };
    private static void Section(Panel panel, string text) => panel.Children.Add(new TextBlock
    {
        Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 2),
    });

    private static void Field(Panel panel, string name, Control control)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("184,*"), ColumnSpacing = 10 };
        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Avalonia.Automation.AutomationProperties.SetName(control, name);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        panel.Children.Add(row);
    }

    private void Flag(Panel panel, string name, bool initial, Action<bool> set)
    {
        var field = new CheckBox { Content = name, IsChecked = initial };
        panel.Children.Add(field);
        _apply.Add(() => set(field.IsChecked == true));
    }

    private static NumericUpDown Numeric(double initial, double min, double max, double step) => new()
    {
        Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step,
        Value = (decimal)Math.Clamp(initial, min, max), FormatString = step >= 1 ? "0" : "0.##", MinWidth = 116,
    };

    private void Number(Panel panel, string name, double initial, double min, double max, double step, Action<double> set)
    {
        var field = Numeric(initial, min, max, step);
        field.HorizontalAlignment = HorizontalAlignment.Left;
        Field(panel, name, field);
        _apply.Add(() => set((double)(field.Value ?? (decimal)initial)));
    }

    private void Choice(Panel panel, string name, string initial, Action<string> set, params (string Value, string Label)[] items)
    {
        var index = Array.FindIndex(items, item => item.Value.Equals(initial, StringComparison.OrdinalIgnoreCase));
        var field = new ComboBox
        {
            ItemsSource = items.Select(item => item.Label).ToArray(),
            SelectedIndex = Math.Max(0, index),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Field(panel, name, field);
        _apply.Add(() => set(items[Math.Max(0, field.SelectedIndex)].Value));
    }

    private void Text(Panel panel, string name, string initial, Action<string> set, string? hint = null,
        bool secret = false, bool folder = false, bool file = false, bool builtInStickers = false)
    {
        var field = new TextBox { Text = initial, Watermark = hint, PasswordChar = secret ? '●' : '\0' };
        if (folder || file)
        {
            var row = new DockPanel { LastChildFill = true };
            var browse = new Button { Content = "浏览", Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(browse, Dock.Right);
            browse.Click += async (_, _) =>
            {
                try
                {
                    var path = folder
                        ? (await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = name })).FirstOrDefault()?.TryGetLocalPath()
                        : (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = name })).FirstOrDefault()?.TryGetLocalPath();
                    if (path is not null)
                        field.Text = path;
                }
                catch (Exception ex) { _status.Text = ex.Message; }
            };
            row.Children.Add(browse);
            if (builtInStickers)
            {
                var preset = new ComboBox { ItemsSource = new[] { "内置贴纸 1", "内置贴纸 2" }, PlaceholderText = "内置贴纸", Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(preset, Dock.Right);
                preset.SelectionChanged += (_, _) =>
                    field.Text = preset.SelectedIndex == 0 ? "builtin:stickers" : "builtin:stickers_v2";
                row.Children.Add(preset);
            }
            row.Children.Add(field);
            Field(panel, name, row);
        }
        else
            Field(panel, name, field);
        _apply.Add(() => set(field.Text?.Trim() ?? ""));
    }

    private sealed class ModeEditor
    {
        private readonly ClipConfig _config;
        private readonly ClipModeSetting _mode;
        private readonly CheckBox _enabled;
        private readonly CheckBox _force;
        private readonly NumericUpDown _cap;
        private readonly NumericUpDown _count;
        private readonly (NumericUpDown Min, NumericUpDown Max)[] _ranges;

        public ModeEditor(ClipConfig config, string key)
        {
            _config = config;
            _mode = config.Mode(key);
            var existing = config.RangesFor(key);
            Body = Fields();
            _enabled = new CheckBox { Content = $"启用{_mode.Label}", IsChecked = _mode.Enabled };
            _force = new CheckBox { Content = "覆盖重生（每次重新渲染）", IsChecked = _mode.Force };
            _count = Numeric(existing.Count, 1, 10, 1);
            _cap = Numeric(config.SimilarityCapByMode.GetValueOrDefault(key, 0.5), 0, 1, 0.05);
            Body.Children.Add(_enabled);
            Body.Children.Add(_force);
            Field(Body, "同模式成片相似度上限", _cap);
            Field(Body, "成片数量", _count);
            var defaultRange = existing[0];
            var batch = RangeFields(defaultRange);
            var batchPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            batchPanel.Children.Add(batch.Min);
            batchPanel.Children.Add(batch.Max);
            var apply = new Button { Content = "应用到全部" };
            batchPanel.Children.Add(apply);
            Field(Body, "批量时长（秒）", batchPanel);
            _ranges = Enumerable.Range(0, 10)
                .Select(i => RangeFields(i < existing.Count ? existing[i] : defaultRange)).ToArray();
            var rows = Fields();
            for (var i = 0; i < _ranges.Length; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(_ranges[i].Min);
                row.Children.Add(new TextBlock { Text = "至", VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(_ranges[i].Max);
                Field(rows, $"第 {i + 1} 条时长（秒）", row);
            }
            Body.Children.Add(rows);
            void RefreshRows()
            {
                for (var i = 0; i < rows.Children.Count; i++)
                    rows.Children[i].IsVisible = i < Count;
            }
            _count.ValueChanged += (_, _) => RefreshRows();
            apply.Click += (_, _) =>
            {
                foreach (var range in _ranges.Take(Count))
                {
                    range.Min.Value = batch.Min.Value;
                    range.Max.Value = batch.Max.Value;
                }
            };
            RefreshRows();
        }

        public StackPanel Body { get; }
        public bool Enabled => _enabled.IsChecked == true;
        private int Count => (int)(_count.Value ?? 1);

        public void Validate()
        {
            for (var i = 0; i < Count; i++)
                if (_ranges[i].Min.Value is not { } min || _ranges[i].Max.Value is not { } max || min > max)
                    throw new InvalidDataException($"{_mode.Label}第 {i + 1} 条的时长范围无效。");
        }

        public void Apply()
        {
            _mode.Enabled = Enabled;
            _mode.Force = _force.IsChecked == true;
            _mode.Count = Count;
            _config.SimilarityCapByMode[_mode.Key] = (double)(_cap.Value ?? 0.5m);
            _config.RangesByMode[_mode.Key] = _ranges.Take(Count).Select(range => new ClipDurationRange
            {
                MinSeconds = (int)range.Min.Value!.Value,
                MaxSeconds = (int)range.Max.Value!.Value,
            }).ToList();
        }

        private static (NumericUpDown Min, NumericUpDown Max) RangeFields(ClipDurationRange range) =>
            (Numeric(range.MinSeconds, 1, 1800, 1), Numeric(range.MaxSeconds, 1, 1800, 1));
    }
}
