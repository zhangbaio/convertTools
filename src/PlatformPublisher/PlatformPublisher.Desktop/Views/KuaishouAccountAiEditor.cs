using PlatformPublisher.Common.Services;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Views;

public sealed class KuaishouAccountAiEditor : UserControl
{
    private readonly ComboBox _textPresets = new() { DisplayMemberBinding = new Binding(nameof(KuaishouTextPreset.Name)) };
    private readonly ComboBox _imagePresets = new() { DisplayMemberBinding = new Binding(nameof(KuaishouImagePreset.Name)) };
    private readonly ComboBox _mode = new() { DisplayMemberBinding = new Binding(nameof(PosterMode.Name)) };
    private readonly TextBox _textKey = new() { PasswordChar = '●' };
    private readonly TextBox _imageKey = new() { PasswordChar = '●' };
    private readonly TextBox _publish = Prompt();
    private readonly TextBox _vertical = Prompt();
    private readonly TextBox _horizontal = Prompt();
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };
    private KuaishouAccountAiOptions _draft = new();
    private ClientSettings _defaults = new();
    private string _clean = "";
    private string _shownTextAddress = "";
    private string _shownImageAddress = "";
    private string _shownPublish = "";
    private string _shownVertical = "";
    private string _shownHorizontal = "";
    private string _shownMode = "";
    private bool _loading;
    internal KuaishouAccountAiStore? Store { get; set; }

    public KuaishouAccountAiEditor()
    {
        var title = new TextBlock { Text = "AI 生成" };
        title.Classes.Add("configurationHeading");
        var panel = new StackPanel { Spacing = 10, Children = { title } };
        panel.Children.Add(Field("AI 文本模型预设", PresetRow(_textPresets, EditTextAsync)));
        panel.Children.Add(Field("AI 文本 API Key", KeyRow(_textKey)));
        panel.Children.Add(Field("发布信息提示词", PromptRow(_publish, () => ClientSettingsDefaults.KuaishouPublishInfoPrompt)));
        panel.Children.Add(Field("AI 图片模型预设", PresetRow(_imagePresets, EditImageAsync)));
        panel.Children.Add(Field("AI 图片 API Key", KeyRow(_imageKey)));
        panel.Children.Add(Field("海报模式", _mode));
        panel.Children.Add(Field("竖屏海报提示词", PromptRow(_vertical,
            () => ClientSettingsDefaults.DefaultKuaishouPosterPrompt(false, _shownMode))));
        panel.Children.Add(Field("横屏封面提示词", PromptRow(_horizontal,
            () => ClientSettingsDefaults.DefaultKuaishouPosterPrompt(true, _shownMode))));
        panel.Children.Add(_feedback);
        Content = panel;
        _textPresets.SelectionChanged += (_, _) => { if (!_loading) ChangeTextPreset(); };
        _imagePresets.SelectionChanged += (_, _) => { if (!_loading) ChangeImagePreset(); };
        _mode.SelectionChanged += (_, _) =>
        {
            if (_loading || _mode.SelectedItem is not PosterMode mode) return;
            CapturePoster();
            ShowPoster(mode);
        };
    }

    internal void LoadAccount(string? accountId)
    {
        IsEnabled = Store is not null && !string.IsNullOrWhiteSpace(accountId);
        _feedback.Text = "";
        _draft = new();
        _defaults = IsEnabled ? ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath) : new();
        if (IsEnabled)
        {
            try { _draft = Store!.Load(accountId!); }
            catch (InvalidOperationException ex) { _feedback.Text = ex.Message; }
        }

        _loading = true;
        try
        {
            SelectPreset(_textPresets, KuaishouAccountAiSettings.DefaultText(_defaults),
                _draft.TextPresets, preset => preset.Id, _draft.TextPresetId);
            SelectPreset(_imagePresets, KuaishouAccountAiSettings.DefaultImage(_defaults),
                _draft.ImagePresets, preset => preset.Id, _draft.ImagePresetId);
            var modes = new[]
            {
                new PosterMode("", $"本机默认（{PosterModeName(_defaults.PosterMode)}）"),
                new PosterMode(ClientSettingsDefaults.PosterModeDirect, "AI 直接生成"),
                new PosterMode(ClientSettingsDefaults.PosterModeEraseFixedTitle, "去字后固定字体写入"),
            };
            _mode.ItemsSource = modes;
            var mode = modes.FirstOrDefault(item => item.Id == _draft.PosterMode) ?? modes[0];
            _mode.SelectedItem = mode;
            ShowPoster(mode);
            _publish.Text = _shownPublish = _draft.PublishPrompt.Length > 0
                ? _draft.PublishPrompt : ClientSettingsStore.ResolveKuaishouPublishInfoPrompt(_defaults);
            ShowKeys();
        }
        finally { _loading = false; }
        CaptureClean();
    }

    internal bool HasChanges => IsEnabled && Snapshot() != _clean;

    internal KuaishouAccountAiOptions? PendingOptions()
    {
        if (!HasChanges) return null;
        return _draft with { TextKeys = new(_draft.TextKeys), ImageKeys = new(_draft.ImageKeys) };
    }

    internal void Save(string accountId, KuaishouAccountAiOptions options) => Store!.Save(accountId, options);

    internal void CaptureClean() => _clean = Snapshot();

    private string Snapshot() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Capture())));

    private KuaishouAccountAiOptions Capture()
    {
        CapturePoster();
        StoreKey(_draft.TextKeys, _shownTextAddress, _textKey.Text);
        StoreKey(_draft.ImageKeys, _shownImageAddress, _imageKey.Text);
        _draft = _draft with
        {
            TextPresetId = (_textPresets.SelectedItem as KuaishouTextPreset)?.Id ?? "",
            ImagePresetId = (_imagePresets.SelectedItem as KuaishouImagePreset)?.Id ?? "",
            PosterMode = (_mode.SelectedItem as PosterMode)?.Id ?? "",
            PublishPrompt = EditedPrompt(_publish, _shownPublish, _draft.PublishPrompt),
        };
        _shownPublish = _publish.Text ?? "";
        return _draft;
    }

    private void ChangeTextPreset()
    {
        StoreKey(_draft.TextKeys, _shownTextAddress, _textKey.Text);
        ShowTextKey();
    }

    private void ChangeImagePreset()
    {
        StoreKey(_draft.ImageKeys, _shownImageAddress, _imageKey.Text);
        ShowImageKey();
    }

    private void ShowKeys()
    {
        ShowTextKey();
        ShowImageKey();
    }

    private void ShowTextKey()
    {
        var preset = _textPresets.SelectedItem as KuaishouTextPreset ?? KuaishouAccountAiSettings.DefaultText(_defaults);
        _shownTextAddress = KuaishouAccountAiOptions.EndpointKey(preset.Endpoint);
        ShowKey(_textKey, _draft.TextKeys, _shownTextAddress,
            KuaishouAccountAiOptions.EndpointKey(_defaults.AiTextEndpoint), _defaults.AiTextApiKey);
    }

    private void ShowImageKey()
    {
        var defaults = KuaishouAccountAiSettings.DefaultImage(_defaults);
        var preset = _imagePresets.SelectedItem as KuaishouImagePreset ?? defaults;
        _shownImageAddress = KuaishouAccountAiOptions.EndpointKey(preset.Endpoint);
        ShowKey(_imageKey, _draft.ImageKeys, _shownImageAddress,
            preset.Provider == defaults.Provider ? KuaishouAccountAiOptions.EndpointKey(defaults.Endpoint) : "",
            defaults.Provider == "ofox_image2" ? _defaults.OfoxImage2ApiKey : _defaults.ImageModelApiKey);
    }

    private static void ShowKey(TextBox box, Dictionary<string, string> keys, string address, string defaultAddress, string defaultKey)
    {
        box.RevealPassword = false;
        box.Text = keys.GetValueOrDefault(address) ?? "";
        box.Watermark = address.Length == 0 ? "请先配置接口地址"
            : address == defaultAddress && !string.IsNullOrWhiteSpace(defaultKey) ? "留空使用本机 API Key"
            : "为当前接口地址填写 API Key";
        box.IsEnabled = address.Length > 0;
    }

    private static void StoreKey(Dictionary<string, string> keys, string address, string? value)
    {
        if (address.Length == 0) return;
        var text = value?.Trim() ?? "";
        if (text.Length == 0) keys.Remove(address);
        else keys[address] = text;
    }

    private void ShowPoster(PosterMode mode)
    {
        _shownMode = mode.Id.Length > 0 ? mode.Id : _defaults.PosterMode;
        var erase = ClientSettingsDefaults.IsEraseFixedTitlePosterMode(_shownMode);
        _vertical.Text = _shownVertical = EffectivePrompt(erase ? _draft.VerticalErasePrompt : _draft.VerticalDirectPrompt, false);
        _horizontal.Text = _shownHorizontal = EffectivePrompt(erase ? _draft.HorizontalErasePrompt : _draft.HorizontalDirectPrompt, true);
    }

    private string EffectivePrompt(string own, bool horizontal) =>
        own.Length > 0 ? own : ClientSettingsStore.ResolveKuaishouPosterPrompt(_defaults, horizontal, _shownMode);

    private void CapturePoster()
    {
        _draft = ClientSettingsDefaults.IsEraseFixedTitlePosterMode(_shownMode)
            ? _draft with
            {
                VerticalErasePrompt = EditedPrompt(_vertical, _shownVertical, _draft.VerticalErasePrompt),
                HorizontalErasePrompt = EditedPrompt(_horizontal, _shownHorizontal, _draft.HorizontalErasePrompt),
            }
            : _draft with
            {
                VerticalDirectPrompt = EditedPrompt(_vertical, _shownVertical, _draft.VerticalDirectPrompt),
                HorizontalDirectPrompt = EditedPrompt(_horizontal, _shownHorizontal, _draft.HorizontalDirectPrompt),
            };
        _shownVertical = _vertical.Text ?? "";
        _shownHorizontal = _horizontal.Text ?? "";
    }

    private static string EditedPrompt(TextBox box, string shown, string stored) =>
        string.Equals(box.Text ?? "", shown, StringComparison.Ordinal) ? stored : box.Text?.Trim() ?? "";

    private static string PosterModeName(string mode) => ClientSettingsDefaults.IsEraseFixedTitlePosterMode(mode)
        ? "去字后固定字体写入" : "AI 直接生成";

    private async Task EditTextAsync(bool create)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _textPresets.SelectedItem is not KuaishouTextPreset selected)
            return;
        var edit = await KuaishouAiPresetDialog.EditTextAsync(owner, selected, create);
        if (edit is null) return;
        _draft = _draft with { TextPresets = ReplacePreset(_draft.TextPresets, edit, item => item.Id) };
        _loading = true;
        try { SelectPreset(_textPresets, KuaishouAccountAiSettings.DefaultText(_defaults), _draft.TextPresets, item => item.Id, edit.Id); }
        finally { _loading = false; }
        ChangeTextPreset();
    }

    private async Task EditImageAsync(bool create)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _imagePresets.SelectedItem is not KuaishouImagePreset selected)
            return;
        var edit = await KuaishouAiPresetDialog.EditImageAsync(owner, selected, create);
        if (edit is null) return;
        _draft = _draft with { ImagePresets = ReplacePreset(_draft.ImagePresets, edit, item => item.Id) };
        _loading = true;
        try { SelectPreset(_imagePresets, KuaishouAccountAiSettings.DefaultImage(_defaults), _draft.ImagePresets, item => item.Id, edit.Id); }
        finally { _loading = false; }
        ChangeImagePreset();
    }

    private static T[] ReplacePreset<T>(IEnumerable<T> presets, T edited, Func<T, string> id) =>
        presets.Where(item => id(item) != id(edited)).Append(edited).ToArray();

    private static void SelectPreset<T>(ComboBox box, T defaults, IEnumerable<T> presets, Func<T, string> id, string selected)
        where T : class
    {
        var items = presets.Prepend(defaults).ToArray();
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(item => id(item) == selected) ?? defaults;
    }

    private static Control PresetRow(ComboBox box, Func<bool, Task> edit)
    {
        var detail = new Button { Content = "详细配置..." };
        var add = new Button { Content = "新建预设..." };
        detail.Click += async (_, _) => await edit(false);
        add.Click += async (_, _) => await edit(true);
        return Row(box, detail, add);
    }

    private static Control KeyRow(TextBox box)
    {
        var toggle = new Button { Content = "显示" };
        toggle.Click += (_, _) => box.RevealPassword = !box.RevealPassword;
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.RevealPasswordProperty)
                toggle.Content = box.RevealPassword ? "隐藏" : "显示";
        };
        return Row(box, toggle);
    }

    private static Control PromptRow(TextBox box, Func<string> defaults)
    {
        var reset = new Button { Content = "恢复默认", VerticalAlignment = VerticalAlignment.Top };
        reset.Click += (_, _) => box.Text = defaults();
        return Row(box, reset);
    }

    private static TextBox Prompt() => new()
    {
        AcceptsReturn = true, MinHeight = 88, MaxHeight = 200, TextWrapping = TextWrapping.Wrap,
        Watermark = "留空时使用本机设置",
    };

    private static Control Row(Control input, params Button[] actions)
    {
        input.Classes.Add("configurationInput");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        foreach (var action in actions) buttons.Children.Add(action);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(input);
        row.Children.Add(buttons);
        return row;
    }

    internal static Control Field(string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        var caption = new TextBlock { Text = label };
        caption.Classes.Add("configurationLabel");
        return new StackPanel { Spacing = 5, Children = { caption, control } };
    }

    private sealed record PosterMode(string Id, string Name);
}
