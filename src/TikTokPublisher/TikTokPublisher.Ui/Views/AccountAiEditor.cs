using System.Net.Http.Headers;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using ShortDrama.Core.Posters;
using ShortDrama.Infrastructure.Files;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class AccountAiEditor : UserControl
{
    private static readonly HttpClient ProbeHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly (string Id, string Name)[] PosterModes =
    [
        (ClientSettingsDefaults.PosterModeDirect, "AI 直接生成"),
        (ClientSettingsDefaults.PosterModeEraseFixedTitle, "去字后固定字体写入"),
    ];
    private readonly ComboBox _textPresets = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _imagePresets = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly WrapPanel _posterMode = new() { Orientation = Orientation.Horizontal };
    private readonly string _posterModeGroup = $"poster-mode-{Guid.NewGuid():N}";
    private readonly TextBox _textKey = KeyBox();
    private readonly TextBox _imageKey = KeyBox();
    private readonly TextBox _publish = PromptBox();
    private readonly TextBlock _imagePromptLabel = new() { Text = "图片提示词" };
    private readonly TextBox _imagePrompt = PromptBox();
    private readonly StackPanel _imagePromptActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<string, TikTokAiTextPreset> _textEdits = new();
    private readonly Dictionary<string, TikTokAiImagePreset> _imageEdits = new();
    private ClientSettings _defaults = new();
    private TikTokAccountAiSettings.Library _library = new();
    private TikTokAccountAiSettings.Keys _savedKeys = new();
    private TikTokAccountAiSettings.Keys _draftKeys = new();
    private IReadOnlyList<SharedAiPreset> _sharedPresets = [];
    private TikTokAccountAiOptions? _draft;
    private string _shownTextKey = "";
    private string _shownImageKey = "";
    private string? _shownTextEndpoint;
    private string? _shownImageEndpoint;
    private bool _refreshingPresets;

    public AccountAiEditor()
    {
        var form = new StackPanel { Spacing = 10 };
        var title = new TextBlock { Text = "AI 生成" };
        title.Classes.Add("configurationHeading");
        form.Children.Add(title);
        form.Children.Add(PresetRow("AI 文本模型", _textPresets, () => EditTextAsync(false), () => EditTextAsync(true)));
        form.Children.Add(KeyRow("AI 文本 API Key", _textKey));
        form.Children.Add(PromptRow("文本提示词", _publish, ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt));
        form.Children.Add(PresetRow("AI 图片模型", _imagePresets, () => EditImageAsync(false), () => EditImageAsync(true)));
        form.Children.Add(KeyRow("AI 图片 API Key", _imageKey));
        form.Children.Add(Field("海报模式", _posterMode));
        var resetImagePrompt = new Button { Content = "恢复默认" };
        resetImagePrompt.Click += (_, _) => _imagePrompt.Text = PosterGenerationTemplates.ResolvePlainText(DraftPosterMode(), null);
        var previewImagePrompt = new Button { Content = "发送预览" };
        previewImagePrompt.Click += async (_, _) => await PreviewImagePromptAsync();
        _imagePromptActions.Children.Add(resetImagePrompt);
        _imagePromptActions.Children.Add(previewImagePrompt);
        form.Children.Add(new StackPanel { Spacing = 4, Children = { _imagePromptLabel, _imagePrompt, _imagePromptActions } });
        _imagePromptActions.IsVisible = false;
        Content = form;
        _textPresets.SelectionChanged += (_, _) => UpdateDisplayedKeys();
        _imagePresets.SelectionChanged += (_, _) => UpdateDisplayedKeys();
        _textPresets.DropDownOpened += (_, _) => RefreshSharedPresets();
        _imagePresets.DropDownOpened += (_, _) => RefreshSharedPresets();
    }

    public void Load(TikTokAccountProfile account, ClientSettings settings)
    {
        _defaults = settings;
        _library = TikTokAccountAiSettings.Load();
        _savedKeys = _library.AccountKeys.GetValueOrDefault(account.Id) ?? new();
        _draftKeys = _savedKeys;
        _sharedPresets = ReadSharedPresets();
        _shownTextEndpoint = _shownImageEndpoint = null;
        var options = account.AiOptions ?? TikTokAccountAiOptions.FromSettings(settings);
        _draft = options with { PosterMode = TikTokAccountAiSettings.ResolvePosterMode(options.PosterMode) };
        _textEdits.Clear();
        _imageEdits.Clear();
        var imageId = _draft.ImagePresetId ?? "";
        if (_library.Image.TryGetValue(imageId, out var image) && !TikTokAccountAiSettings.IsVolcanoPreset(image))
            imageId = "";
        _textKey.RevealPassword = _imageKey.RevealPassword = false;
        _textKey.Text = _shownTextKey = "";
        _imageKey.Text = _shownImageKey = "";
        RefreshPresets(_draft.TextPresetId, imageId);
        _publish.Text = TikTokAccountAiSettings.ResolvePublishPrompt(_draft.PublishPrompt);
        LoadPosterModeSelection();
        UpdateImagePrompt();
        IsEnabled = true;
    }

    public void Clear()
    {
        _draft = null;
        _savedKeys = new();
        _draftKeys = new();
        _shownTextEndpoint = _shownImageEndpoint = null;
        _shownTextKey = _shownImageKey = "";
        _textKey.Text = _imageKey.Text = _publish.Text = "";
        _imagePromptLabel.Text = "图片提示词";
        _imagePrompt.Text = "";
        _imagePromptActions.IsVisible = false;
        _posterMode.Children.Clear();
        _textPresets.Items.Clear();
        _imagePresets.Items.Clear();
        _textEdits.Clear();
        _imageEdits.Clear();
        IsEnabled = false;
    }

    public void Save(TikTokAccountProfile account)
    {
        if (_draft is null)
            return;

        var keys = ReadKeys();
        if (keys != _savedKeys || _textEdits.Count > 0 || _imageEdits.Count > 0)
            TikTokAccountAiSettings.Save(account.Id, keys, _textEdits.Values, _imageEdits.Values);
        account.AiOptions = CapturePosterPrompt(_draft) with
        {
            TextPresetId = SelectedId(_textPresets),
            ImagePresetId = SelectedId(_imagePresets),
            PublishPrompt = ReadPublishPrompt(),
        };
    }

    private TikTokAccountAiSettings.Keys ReadKeys() => new(
        Read(_textKey) == _shownTextKey || _shownTextEndpoint is not { } textEndpoint
            ? _draftKeys.Text : new(textEndpoint, Read(_textKey)),
        Read(_imageKey) == _shownImageKey || _shownImageEndpoint is not { } imageEndpoint
            ? _draftKeys.Image : new(imageEndpoint, Read(_imageKey)));

    internal object CaptureDraftState() => new
    {
        Draft = _draft is { } draft ? CapturePosterPrompt(draft) : null,
        Keys = ReadKeys(),
        TextPresets = _textEdits.Values.ToArray(),
        ImagePresets = _imageEdits.Values.ToArray(),
        PublishPrompt = ReadPublishPrompt(),
    };

    private string ReadPublishPrompt()
    {
        var prompt = Read(_publish);
        return string.Equals(prompt.ReplaceLineEndings("\n"),
            ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt.ReplaceLineEndings("\n"), StringComparison.Ordinal) ? "" : prompt;
    }

    private void LoadPosterModeSelection()
    {
        var mode = DraftPosterMode();
        _posterMode.Children.Clear();
        foreach (var (id, name) in PosterModes)
            AddPosterModeOption(id, name, id == mode);

        if (!PosterModes.Any(choice => choice.Id == mode))
            AddPosterModeOption(mode, PosterModeName(mode), selected: true);
    }

    private void AddPosterModeOption(string id, string name, bool selected)
    {
        var option = new RadioButton
        {
            Content = name,
            Tag = id,
            GroupName = _posterModeGroup,
            IsChecked = selected,
            Margin = new Thickness(0, 0, 18, 4),
        };
        _posterMode.Children.Add(option);
        option.IsCheckedChanged += (_, _) =>
        {
            if (_draft is null || option.IsChecked != true)
                return;

            _draft = CapturePosterPrompt(_draft) with { PosterMode = id };
            UpdateImagePrompt();
        };
    }

    private string DraftPosterMode() => (_draft?.PosterMode ?? ClientSettingsDefaults.PosterMode).Trim().ToLowerInvariant();

    private static string PosterModeName(string mode) => PosterModes.FirstOrDefault(choice => choice.Id == mode).Name ?? (mode switch
    {
        "video_frame" => "视频抽帧生成封面",
        "ai" or "poster_ai_edit" => "参考原海报生成封面",
        "poster_ai_erase_pil_title" => "AI 去字后重绘标题",
        _ => "沿用原图",
    });

    private TikTokAccountAiOptions CapturePosterPrompt(TikTokAccountAiOptions draft)
    {
        var text = _imagePrompt.Text;
        var prompt = string.IsNullOrWhiteSpace(text) ||
                     string.Equals(text.ReplaceLineEndings("\n"),
                         PosterGenerationTemplates.ResolvePlainText(draft.PosterMode, null).ReplaceLineEndings("\n"),
                         StringComparison.Ordinal) ? null : text;
        return draft.PosterMode switch
        {
            PosterGenerationTemplates.DirectMode => draft with { DirectPosterPrompt = prompt },
            PosterGenerationTemplates.EraseMode => draft with { ErasePosterPrompt = prompt },
            _ => draft,
        };
    }

    private async Task PreviewImagePromptAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner ||
            !PosterGenerationTemplates.SupportsPlainText(DraftPosterMode()))
            return;

        var mode = DraftPosterMode();
        var body = _imagePrompt.Text;
        var fields = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        var prompt = PromptBox(readOnly: true);
        prompt.MaxHeight = 420;
        if (mode == PosterGenerationTemplates.DirectMode)
        {
            var title = Input("示例剧名");
            fields.Children.Add(Field("预览剧名", title));
            title.TextChanged += (_, _) => prompt.Text = PosterGenerationTemplates.ComposePlainText(mode, body, title.Text ?? "");
            prompt.Text = PosterGenerationTemplates.ComposePlainText(mode, body, title.Text ?? "");
        }
        else
        {
            prompt.Text = PosterGenerationTemplates.ComposePlainText(mode, body, "");
        }
        fields.Children.Add(Field("发送内容", prompt));
        var dialog = new Window
        {
            Title = "图片提示词发送预览", Width = 640, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = fields,
        };
        await dialog.ShowDialog(owner);
    }

    private void UpdateImagePrompt()
    {
        var mode = DraftPosterMode();
        _imagePromptLabel.Text = $"图片提示词（{PosterModeName(mode)}）";
        var usesVisiblePrompt = PosterGenerationTemplates.SupportsPlainText(mode);
        _imagePrompt.IsReadOnly = !usesVisiblePrompt;
        _imagePromptActions.IsVisible = usesVisiblePrompt;
        if (usesVisiblePrompt)
        {
            _imagePrompt.Text = PosterGenerationTemplates.ResolvePlainText(mode,
                mode == PosterGenerationTemplates.EraseMode ? _draft?.ErasePosterPrompt : _draft?.DirectPosterPrompt);
            return;
        }

        _imagePrompt.Text = mode switch
        {
            "video_frame" or "ai" or "poster_ai_edit" => PosterRenamer.BuildImageGenerationPrompt(_defaults.FrameCoverPrompt, "示例剧名"),
            "poster_ai_erase_pil_title" => PosterRenamer.DefaultPosterAllTextErasePrompt,
            _ => "当前模式直接使用原图，无需调用图片模型。",
        };
    }

    private TikTokAiTextPreset? SelectedText()
    {
        var id = SelectedId(_textPresets);
        if (FindSharedPreset(id, SharedAiPresetKind.Text) is { } shared)
            return new(shared.Id, shared.Name, shared.Endpoint, shared.Model, shared.Timeout);
        return id.Length == 0 ? TikTokAccountAiSettings.DefaultText(_defaults)
            : _textEdits.GetValueOrDefault(id) ?? _library.Text.GetValueOrDefault(id);
    }

    private TikTokAiImagePreset? SelectedImage()
    {
        var id = SelectedId(_imagePresets);
        if (FindSharedPreset(id, SharedAiPresetKind.Image) is { } shared)
            return new(shared.Id, shared.Name, shared.Endpoint, shared.Model, "2K", shared.Ratio);
        return id.Length == 0 ? TikTokAccountAiSettings.DefaultImage(_defaults)
            : _imageEdits.GetValueOrDefault(id) ?? _library.Image.GetValueOrDefault(id);
    }

    private void UpdateDisplayedKeys()
    {
        if (_refreshingPresets || _draft is null)
            return;

        _draftKeys = ReadKeys();
        var sharedText = SharedAiPresetStore.IsShared(SelectedId(_textPresets));
        var sharedImage = SharedAiPresetStore.IsShared(SelectedId(_imagePresets));
        _shownTextEndpoint = sharedText ? null : SelectedText()?.Endpoint;
        _shownImageEndpoint = sharedImage ? null : SelectedImage()?.Endpoint;
        ShowKey(_textKey, sharedText, _shownTextEndpoint, _draftKeys.Text,
            _defaults.AiTextEndpoint, _defaults.AiTextApiKey, out _shownTextKey);
        ShowKey(_imageKey, sharedImage, _shownImageEndpoint, _draftKeys.Image,
            TikTokAccountAiSettings.DefaultImage(_defaults).Endpoint, _defaults.ImageModelApiKey, out _shownImageKey);
    }

    private static void ShowKey(TextBox box, bool shared, string? endpoint, TikTokAccountAiSettings.BoundKey? key,
        string defaultEndpoint, string defaultKey, out string displayed)
    {
        box.IsEnabled = !shared;
        box.Watermark = shared ? "使用系统设置中的 API Key" : "";
        displayed = endpoint is null ? "" : TikTokAccountAiSettings.ResolveKey(key, endpoint, defaultEndpoint, defaultKey);
        box.Text = displayed;
    }

    private SharedAiPreset? FindSharedPreset(string id, SharedAiPresetKind kind) =>
        _sharedPresets.FirstOrDefault(preset => preset.Id == id && preset.Kind == kind);

    private static IReadOnlyList<SharedAiPreset> ReadSharedPresets()
    {
        try { return SharedAiPresetStore.List(); }
        catch (InvalidOperationException) { return []; }
    }

    private void RefreshSharedPresets()
    {
        if (_draft is null)
            return;
        var latest = ReadSharedPresets();
        if (_sharedPresets.SequenceEqual(latest))
            return;
        _sharedPresets = latest;
        RefreshPresets(SelectedId(_textPresets), SelectedId(_imagePresets));
    }

    private IEnumerable<(string Id, string Name)> SharedChoices(SharedAiPresetKind kind) =>
        _sharedPresets.Where(preset => preset.Kind == kind).Select(preset => (preset.Id, $"共享 · {preset.Name}"));

    private void RefreshPresets(string textId, string imageId)
    {
        _refreshingPresets = true;
        try
        {
            Fill(_textPresets, textId, _library.Text.Values.Concat(_textEdits.Values)
                .GroupBy(preset => preset.Id).Select(group => (group.Key, group.Last().Name))
                .Concat(SharedChoices(SharedAiPresetKind.Text)));
            Fill(_imagePresets, imageId, _library.Image.Values.Concat(_imageEdits.Values)
                .Where(TikTokAccountAiSettings.IsVolcanoPreset)
                .GroupBy(preset => preset.Id).Select(group => (group.Key, group.Last().Name))
                .Concat(SharedChoices(SharedAiPresetKind.Image)));
        }
        finally
        {
            _refreshingPresets = false;
        }
        UpdateDisplayedKeys();
    }

    private static void Fill(ComboBox box, string? id, IEnumerable<(string Id, string Name)> items)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "本机默认", Tag = "" });
        foreach (var item in items.OrderBy(item => item.Name))
            box.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Id });
        var selected = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, id ?? ""));
        if (selected is null)
        {
            selected = new ComboBoxItem { Content = "预设缺失（请选择其他预设）", Tag = id };
            box.Items.Add(selected);
        }
        box.SelectedItem = selected;
    }

    private async Task EditTextAsync(bool create)
    {
        if (_draft is null || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var draft = _draft;
        var id = SelectedId(_textPresets);
        var current = SelectedText();
        var preset = current ?? TikTokAccountAiSettings.DefaultText(_defaults);
        var readOnly = !create && SharedAiPresetStore.IsShared(id);
        var name = Input(create ? "" : preset.Name);
        var endpoint = Input(preset.Endpoint);
        var model = Input(preset.Model);
        var timeout = Number(preset.Timeout, 5, 600);
        name.IsReadOnly = endpoint.IsReadOnly = model.IsReadOnly = readOnly;
        timeout.IsEnabled = !readOnly;
        var dialog = new DetailWindow(owner, "AI 文本详细配置", create);
        dialog.Add("预设名称", name);
        dialog.Add("接口地址", endpoint);
        dialog.Add("模型名称", model);
        dialog.Add("请求超时（秒）", timeout);
        var probe = new Button { Content = "测试 AI 文本", IsVisible = !readOnly };
        dialog.Fields.Children.Add(probe);
        if (readOnly)
            dialog.Fields.Children.Add(new TextBlock { Text = "在系统设置 → AI 文本中编辑共享预设。" });
        probe.Click += async (_, _) =>
        {
            probe.IsEnabled = false;
            try
            {
                var key = Read(_textKey);
                if ((current is null || !TikTokAccountAiSettings.SameEndpoint(Read(endpoint), preset.Endpoint)) && key == _shownTextKey)
                {
                    dialog.Message.Text = "接口地址已变更。请先在发布配置输入该接口的 Key。";
                    return;
                }
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds((int)(timeout.Value ?? preset.Timeout)));
                dialog.Message.Text = await ProbeTextAsync(Read(endpoint), Read(model), key, ct.Token);
            }
            catch (Exception)
            {
                dialog.Message.Text = "测试失败，请核对接口地址、模型和网络。";
            }
            finally
            {
                probe.IsEnabled = true;
            }
        };
        dialog.Validate = () => readOnly ? null : ValidateConnection(name, endpoint, model);
        if (!await dialog.ShowDialog<bool>(owner) || !ReferenceEquals(_draft, draft) || readOnly)
            return;

        var connectionChanged = !TikTokAccountAiSettings.SameEndpoint(Read(endpoint), preset.Endpoint)
            || Read(model) != preset.Model || (int)(timeout.Value ?? preset.Timeout) != preset.Timeout;
        if (create || current is null || connectionChanged || Read(name) != preset.Name)
        {
            id = create || preset.Id.Length == 0 ? Guid.NewGuid().ToString("N") : preset.Id;
            var savedName = !create && preset.Id.Length == 0 && Read(name) == preset.Name ? Read(model) : Read(name);
            _textEdits[id] = new(id, savedName, Read(endpoint), Read(model), (int)(timeout.Value ?? preset.Timeout));
        }
        RefreshPresets(id, SelectedId(_imagePresets));
    }

    private async Task EditImageAsync(bool create)
    {
        if (_draft is null || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var draft = _draft;
        var id = SelectedId(_imagePresets);
        var current = SelectedImage();
        var preset = current ?? TikTokAccountAiSettings.DefaultImage(_defaults);
        var readOnly = !create && SharedAiPresetStore.IsShared(id);
        var name = Input(create ? "" : preset.Name);
        var endpoint = Input(preset.Endpoint);
        var model = Input(preset.Model);
        var resolution = Input(preset.Resolution);
        var ratio = Input(preset.Ratio);
        name.IsReadOnly = endpoint.IsReadOnly = model.IsReadOnly = resolution.IsReadOnly = ratio.IsReadOnly = readOnly;
        var usesVisiblePrompt = PosterGenerationTemplates.IsSupportedMode(DraftPosterMode());
        var verify = new CheckBox
        {
            Content = usesVisiblePrompt ? "启用标题校验（仅报告）" : "启用标题校验",
            IsChecked = draft.VerifyTitle,
        };
        var verifyMode = usesVisiblePrompt ? null
            : Choice(draft.VerifyMode, ("fallback_repaint", "AI 重试后重绘"), ("warn", "AI 重试后仅警告"), ("blocking", "AI 重试后阻断"));
        var dialog = new DetailWindow(owner, "AI 图片详细配置", create);
        dialog.Add("预设名称", name);
        dialog.Add("模型 Endpoint", endpoint);
        dialog.Add("模型 ID", model);
        dialog.Add("分辨率", resolution);
        dialog.Add("比例", ratio);
        if (readOnly)
            dialog.Fields.Children.Add(new TextBlock { Text = "在系统设置 → AI 图片中编辑共享预设。" });
        dialog.Fields.Children.Add(verify);
        if (verifyMode is not null)
            dialog.Add("校验失败处理", verifyMode);
        dialog.Validate = () => readOnly ? null : ValidateConnection(name, endpoint, model);
        if (!await dialog.ShowDialog<bool>(owner) || !ReferenceEquals(_draft, draft))
            return;

        var connectionChanged = !TikTokAccountAiSettings.SameEndpoint(Read(endpoint), preset.Endpoint)
            || Read(model) != preset.Model || Read(resolution) != preset.Resolution || Read(ratio) != preset.Ratio;
        if (!readOnly && (create || current is null || connectionChanged || Read(name) != preset.Name))
        {
            id = create || preset.Id.Length == 0 ? Guid.NewGuid().ToString("N") : preset.Id;
            var savedName = !create && preset.Id.Length == 0 && Read(name) == preset.Name ? Read(model) : Read(name);
            _imageEdits[id] = new(id, savedName, Read(endpoint), Read(model), Read(resolution), Read(ratio));
        }
        _draft = draft with
        {
            VerifyTitle = verify.IsChecked == true,
            VerifyMode = verifyMode is null ? draft.VerifyMode : SelectedId(verifyMode),
        };
        RefreshPresets(SelectedId(_textPresets), id);
    }

    private static async Task<string> ProbeTextAsync(string endpoint, string model, string key, CancellationToken ct)
    {
        if (!ValidEndpoint(endpoint) || model.Length == 0 || key.Length == 0)
            return "请填写有效的接口地址、模型和 API Key。";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new
        {
            model,
            thinking = new { type = "disabled" },
            messages = new[] { new { role = "user", content = "回复 OK" } },
            max_tokens = 8,
        });
        using var response = await ProbeHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return response.IsSuccessStatusCode ? "AI 文本接口可用。" : $"AI 文本测试失败（HTTP {(int)response.StatusCode}）。";
    }

    private static bool ValidEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            && string.IsNullOrEmpty(uri.UserInfo);

    private static string? ValidateConnection(TextBox name, TextBox endpoint, TextBox model) =>
        Read(name).Length == 0 || !ValidEndpoint(Read(endpoint)) || Read(model).Length == 0
            ? "请填写预设名称、有效的 HTTP(S) 接口地址和模型。"
            : null;

    private static string Read(TextBox box) => box.Text?.Trim() ?? "";
    private static string SelectedId(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private static TextBox Input(string text = "") => new() { Text = text, Classes = { "configurationInput" } };
    private static TextBox KeyBox() => new() { PasswordChar = '●', Classes = { "configurationInput" } };
    private static TextBox PromptBox(bool readOnly = false, string? text = null) => new()
    {
        AcceptsReturn = true, IsReadOnly = readOnly, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 150,
        Text = text, Watermark = readOnly ? null : "留空使用默认提示词", Classes = { "configurationInput" },
    };
    private static NumericUpDown Number(int value, int min, int max) =>
        new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), FormatString = "0", Classes = { "configurationInput" } };

    private static ComboBox Choice(string selected, params (string Id, string Name)[] choices)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "configurationInput" } };
        foreach (var (id, name) in choices)
            box.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected)) ?? box.Items[0];
        return box;
    }

    private static Control PresetRow(string label, ComboBox box, Func<Task> edit, Func<Task> create)
    {
        var details = new Button { Content = "详细配置…" };
        var add = new Button { Content = "新建预设…" };
        details.Click += async (_, _) => await edit();
        add.Click += async (_, _) => await create();
        return Field(label, Row(box, details, add));
    }

    private static Control KeyRow(string label, TextBox box)
    {
        var toggle = new Button { Content = "显示" };
        toggle.Click += (_, _) => toggle.Content = (box.RevealPassword = !box.RevealPassword) ? "隐藏" : "显示";
        return Field(label, Row(box, toggle));
    }

    private static Control PromptRow(string label, TextBox box, string defaultPrompt)
    {
        var reset = new Button { Content = "恢复默认", VerticalAlignment = VerticalAlignment.Top };
        reset.Click += (_, _) => box.Text = defaultPrompt;
        return Field(label, Row(box, reset));
    }

    private static Grid Row(params Control[] controls)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        for (var index = 0; index < controls.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(index == 0 ? GridLength.Star : GridLength.Auto));
            Grid.SetColumn(controls[index], index);
            grid.Children.Add(controls[index]);
        }
        return grid;
    }

    private static StackPanel Field(string label, Control input) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, Classes = { "configurationLabel" } }, input },
    };

    private sealed class DetailWindow : Window
    {
        public StackPanel Fields { get; } = new() { Spacing = 9, Margin = new Thickness(18) };
        public TextBlock Message { get; } = new() { TextWrapping = TextWrapping.Wrap };
        public Func<string?>? Validate { get; set; }

        public DetailWindow(Window owner, string title, bool create)
        {
            Styles.Add(new StyleInclude((Uri?)null)
            {
                Source = new Uri("avares://TikTokPublisher.Ui/Themes/ConfigurationFormStyles.axaml"),
            });
            Classes.Set("compact", owner.Classes.Contains("compact"));
            Title = title;
            Width = 560;
            Height = 650;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Fields.Children.Add(new TextBlock
            {
                Text = create ? "连接参数保存在本机。生成选项应用于当前账号。"
                    : "修改已共享的预设会影响使用它的账号。生成选项应用于当前账号。",
                TextWrapping = TextWrapping.Wrap,
            });
            var cancel = new Button { Content = "取消", IsCancel = true };
            cancel.Click += (_, _) => Close(false);
            var confirm = new Button { Content = "确定", IsDefault = true, Classes = { "primaryAction" } };
            confirm.Click += (_, _) =>
            {
                Message.Text = Validate?.Invoke();
                if (string.IsNullOrEmpty(Message.Text))
                    Close(true);
            };
            var footer = new StackPanel
            {
                Margin = new Thickness(18), Spacing = 8,
                Children = { Message, new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, confirm },
                } },
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            Content = new DockPanel { Children = { footer, new ScrollViewer { Content = Fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } } };
        }

        public void Add(string label, Control input) => Fields.Children.Add(Field(label, input));
    }
}
