using System.Net.Http.Headers;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class AccountAiEditor : UserControl
{
    private static readonly HttpClient ProbeHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly ComboBox _textPresets = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _imagePresets = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _textKey = KeyBox();
    private readonly TextBox _imageKey = KeyBox();
    private readonly TextBox _publish = PromptBox();
    private readonly Dictionary<string, TikTokAiTextPreset> _textEdits = new();
    private readonly Dictionary<string, TikTokAiImagePreset> _imageEdits = new();
    private ClientSettings _defaults = new();
    private TikTokAccountAiSettings.Library _library = new();
    private TikTokAccountAiSettings.Keys _savedKeys = new();
    private TikTokAccountAiOptions? _draft;
    private string _shownTextKey = "";
    private string _shownImageKey = "";
    private bool _refreshingPresets;

    public AccountAiEditor()
    {
        var form = new StackPanel { Spacing = 10 };
        var title = new TextBlock { Text = "AI 生成" };
        title.Classes.Add("configurationHeading");
        form.Children.Add(title);
        form.Children.Add(PresetRow("AI 文本模型预设", _textPresets, () => EditTextAsync(false), () => EditTextAsync(true)));
        form.Children.Add(KeyRow("AI 文本 API Key", _textKey));
        form.Children.Add(PromptRow("发布信息提示词", _publish));
        form.Children.Add(PresetRow("AI 图片模型预设", _imagePresets, () => EditImageAsync(false), () => EditImageAsync(true)));
        form.Children.Add(KeyRow("AI 图片 API Key", _imageKey));
        Content = form;
        _textPresets.SelectionChanged += (_, _) => UpdateDisplayedKeys();
        _imagePresets.SelectionChanged += (_, _) => UpdateDisplayedKeys();
    }

    public void Load(TikTokAccountProfile account, ClientSettings settings)
    {
        _defaults = settings;
        _library = TikTokAccountAiSettings.Load();
        _savedKeys = _library.AccountKeys.GetValueOrDefault(account.Id) ?? new();
        _draft = account.AiOptions ?? TikTokAccountAiOptions.FromSettings(settings);
        _textEdits.Clear();
        _imageEdits.Clear();
        RefreshPresets(_draft.TextPresetId, _draft.ImagePresetId);
        _textKey.RevealPassword = _imageKey.RevealPassword = false;
        _textKey.Text = _shownTextKey = "";
        _imageKey.Text = _shownImageKey = "";
        UpdateDisplayedKeys();
        _publish.Text = _draft.PublishPrompt;
        IsEnabled = true;
    }

    public void Clear()
    {
        _draft = null;
        _savedKeys = new();
        _shownTextKey = _shownImageKey = "";
        _textKey.Text = _imageKey.Text = _publish.Text = "";
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
        account.AiOptions = _draft with
        {
            TextPresetId = SelectedId(_textPresets),
            ImagePresetId = SelectedId(_imagePresets),
            PublishPrompt = Read(_publish),
        };
    }

    private TikTokAccountAiSettings.Keys ReadKeys() => new(
        Read(_textKey) == _shownTextKey || SelectedText() is not { } text
            ? _savedKeys.Text : new(text.Endpoint, Read(_textKey)),
        Read(_imageKey) == _shownImageKey || SelectedImage() is not { } image
            ? _savedKeys.Image : new(image.Endpoint, Read(_imageKey)));

    private TikTokAiTextPreset? SelectedText()
    {
        var id = SelectedId(_textPresets);
        return id.Length == 0 ? TikTokAccountAiSettings.DefaultText(_defaults)
            : _textEdits.GetValueOrDefault(id) ?? _library.Text.GetValueOrDefault(id);
    }

    private TikTokAiImagePreset? SelectedImage()
    {
        var id = SelectedId(_imagePresets);
        return id.Length == 0 ? TikTokAccountAiSettings.DefaultImage(_defaults)
            : _imageEdits.GetValueOrDefault(id) ?? _library.Image.GetValueOrDefault(id);
    }

    private void UpdateDisplayedKeys()
    {
        if (_refreshingPresets || _draft is null)
            return;

        var text = SelectedText();
        var image = SelectedImage();
        if (Read(_textKey) == _shownTextKey)
            _textKey.Text = _shownTextKey = text is null ? "" : TikTokAccountAiSettings.ResolveKey(
                _savedKeys.Text, text.Endpoint, _defaults.AiTextEndpoint, _defaults.AiTextApiKey);
        if (Read(_imageKey) == _shownImageKey)
            _imageKey.Text = _shownImageKey = image is null ? "" : TikTokAccountAiSettings.ResolveKey(
                _savedKeys.Image, image.Endpoint, TikTokAccountAiSettings.DefaultImage(_defaults).Endpoint,
                TikTokAccountAiSettings.DefaultImageKey(_defaults));
    }

    private void RefreshPresets(string textId, string imageId)
    {
        _refreshingPresets = true;
        try
        {
            Fill(_textPresets, textId, _library.Text.Values.Concat(_textEdits.Values)
                .GroupBy(preset => preset.Id).Select(group => (group.Key, group.Last().Name)));
            Fill(_imagePresets, imageId, _library.Image.Values.Concat(_imageEdits.Values)
                .GroupBy(preset => preset.Id).Select(group => (group.Key, group.Last().Name)));
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
            selected = new ComboBoxItem { Content = "预设缺失（请选择本机预设）", Tag = id };
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
        var name = Input(create ? "" : preset.Name);
        var endpoint = Input(preset.Endpoint);
        var model = Input(preset.Model);
        var timeout = Number(preset.Timeout, 5, 600);
        var role = Choice(draft.RoleSelectionMode, ("local", "本地链路"), ("ai_full_review", "AI 全量优选"));
        var fallback = new CheckBox { Content = "全量优选失败时回退本地链路", IsChecked = draft.RoleFallback };
        var dialog = new DetailWindow("AI 文本详细配置", create);
        dialog.Add("预设名称", name);
        dialog.Add("接口地址", endpoint);
        dialog.Add("模型名称", model);
        dialog.Add("请求超时（秒）", timeout);
        dialog.Add("角色参考图筛选", role);
        dialog.Fields.Children.Add(fallback);
        var probe = new Button { Content = "测试 AI 文本" };
        dialog.Fields.Children.Add(probe);
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
        dialog.Validate = () => ValidateConnection(name, endpoint, model);
        if (!await dialog.ShowDialog<bool>(owner) || !ReferenceEquals(_draft, draft))
            return;

        var connectionChanged = !TikTokAccountAiSettings.SameEndpoint(Read(endpoint), preset.Endpoint)
            || Read(model) != preset.Model || (int)(timeout.Value ?? preset.Timeout) != preset.Timeout;
        if (create || current is null || connectionChanged || Read(name) != preset.Name)
        {
            id = create || preset.Id.Length == 0 ? Guid.NewGuid().ToString("N") : preset.Id;
            var savedName = !create && preset.Id.Length == 0 && Read(name) == preset.Name ? Read(model) : Read(name);
            _textEdits[id] = new(id, savedName, Read(endpoint), Read(model), (int)(timeout.Value ?? preset.Timeout));
        }
        _draft = draft with { RoleSelectionMode = SelectedId(role), RoleFallback = fallback.IsChecked == true };
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
        var name = Input(create ? "" : preset.Name);
        var endpoint = Input(preset.Endpoint);
        var model = Input(preset.Model);
        var provider = Choice(preset.Provider, ("doubao", "豆包"), ("ofox_image2", "Ofox Image 2"));
        var resolution = Input(preset.Resolution);
        var ratio = Input(preset.Ratio);
        var mode = Choice(draft.PosterMode, (ClientSettingsDefaults.PosterModeDirect, "AI 直接生成"),
            (ClientSettingsDefaults.PosterModeEraseFixedTitle, "去字后固定字体写入"));
        var verify = new CheckBox { Content = "启用标题校验", IsChecked = draft.VerifyTitle };
        var verifyMode = Choice(draft.VerifyMode, ("fallback_repaint", "AI 重试后重绘"), ("warn", "AI 重试后仅警告"), ("blocking", "AI 重试后阻断"));
        var retries = Number(draft.VerifyRetries, 0, 3);
        var roleView = Choice(draft.RoleViewMode, ("multi_angle", "多角度"), ("single", "单图"));
        var dialog = new DetailWindow("AI 图片详细配置", create);
        dialog.Add("预设名称", name);
        dialog.Add("图片接口类型", provider);
        dialog.Add("模型 Endpoint", endpoint);
        dialog.Add("模型 ID", model);
        dialog.Add("分辨率 / Image 2 尺寸", resolution);
        dialog.Add("比例 / Image 2 质量", ratio);
        dialog.Add("海报模式", mode);
        dialog.Add("角色定妆视图", roleView);
        dialog.Fields.Children.Add(verify);
        dialog.Add("校验失败处理", verifyMode);
        dialog.Add("AI 修复重试", retries);
        dialog.Validate = () => ValidateConnection(name, endpoint, model);
        if (!await dialog.ShowDialog<bool>(owner) || !ReferenceEquals(_draft, draft))
            return;

        var connectionChanged = !TikTokAccountAiSettings.SameEndpoint(Read(endpoint), preset.Endpoint)
            || Read(model) != preset.Model || Read(resolution) != preset.Resolution || Read(ratio) != preset.Ratio
            || SelectedId(provider) != preset.Provider;
        if (create || current is null || connectionChanged || Read(name) != preset.Name)
        {
            id = create || preset.Id.Length == 0 ? Guid.NewGuid().ToString("N") : preset.Id;
            var savedName = !create && preset.Id.Length == 0 && Read(name) == preset.Name ? Read(model) : Read(name);
            _imageEdits[id] = new(id, savedName, Read(endpoint), Read(model), Read(resolution), Read(ratio), SelectedId(provider));
        }
        _draft = draft with
        {
            PosterMode = SelectedId(mode),
            RoleViewMode = SelectedId(roleView),
            VerifyTitle = verify.IsChecked == true,
            VerifyMode = SelectedId(verifyMode),
            VerifyRetries = (int)(retries.Value ?? draft.VerifyRetries),
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
    private static TextBox PromptBox() => new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 150,
        Watermark = "留空使用默认提示词", Classes = { "configurationInput" },
    };
    private static NumericUpDown Number(int value, int min, int max) =>
        new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), FormatString = "0" };

    private static ComboBox Choice(string selected, params (string Id, string Name)[] choices)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
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

    private static Control PromptRow(string label, TextBox box)
    {
        var reset = new Button { Content = "恢复默认", VerticalAlignment = VerticalAlignment.Top };
        reset.Click += (_, _) => box.Text = "";
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

        public DetailWindow(string title, bool create)
        {
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
            Content = new DockPanel { Children = { footer, new ScrollViewer { Content = Fields } } };
        }

        public void Add(string label, Control input) => Fields.Children.Add(Field(label, input));
    }
}
