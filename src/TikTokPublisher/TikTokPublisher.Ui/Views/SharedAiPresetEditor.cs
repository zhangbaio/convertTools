using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class SharedAiPresetEditor : UserControl
{
    private readonly ComboBox _presets = new() { DisplayMemberBinding = new Binding(nameof(SharedAiPreset.Name)) };
    private readonly TextBox _name = Input();
    private readonly TextBox _endpoint = Input();
    private readonly TextBox _model = Input();
    private readonly TextBox _key = Input();
    private readonly NumericUpDown _timeout = new() { Minimum = 5, Maximum = 600, FormatString = "0" };
    private readonly ComboBox _ratio = new()
    {
        ItemsSource = new[] { "3:4", "9:16", "2:3", "4:3", "16:9", "3:2", "1:1", "21:9", "auto" },
    };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private SharedAiPresetKind _kind;
    private SharedAiPreset? _current;
    private bool _refreshing;

    internal void Configure(SharedAiPresetKind kind)
    {
        _kind = kind;
        _key.PasswordChar = '●';
        _message.Classes.Add("pageSummary");

        var panel = new StackPanel { Spacing = 10, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        var header = new TextBlock
        {
            Text = kind == SharedAiPresetKind.Text ? "共享豆包文本预设" : "共享豆包图片预设",
            FontWeight = FontWeight.SemiBold,
        };
        panel.Children.Add(header);
        var add = new Button { Content = "新建预设" };
        add.Click += (_, _) =>
        {
            _presets.SelectedItem = null;
            ShowPreset(null);
        };
        panel.Children.Add(Field("预设", WithButton(_presets, add)));
        panel.Children.Add(Field("预设名称", _name));
        panel.Children.Add(Field("API 地址", _endpoint));
        panel.Children.Add(Field("模型 ID", _model));
        panel.Children.Add(Field("API Key", _key));
        panel.Children.Add(kind == SharedAiPresetKind.Text
            ? Field("请求超时（秒）", _timeout)
            : Field("图片比例（2K）", _ratio));

        var save = new Button { Content = "保存预设", HorizontalAlignment = HorizontalAlignment.Left };
        save.Classes.Add("primaryAction");
        save.Click += (_, _) => Save();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { save, _message } });
        Content = panel;
        _presets.SelectionChanged += (_, _) =>
        {
            if (!_refreshing)
                ShowPreset(_presets.SelectedItem as SharedAiPreset);
        };
    }

    internal void Refresh() => Refresh(_current?.Id);

    private void Refresh(string? selection)
    {
        _refreshing = true;
        try
        {
            var items = SharedAiPresetStore.List().Where(preset => preset.Kind == _kind).ToArray();
            _presets.ItemsSource = items;
            _presets.SelectedItem = items.FirstOrDefault(item => item.Id == selection);
            ShowPreset(_presets.SelectedItem as SharedAiPreset);
        }
        catch (InvalidOperationException ex)
        {
            _message.Text = ex.Message;
        }
        finally { _refreshing = false; }
    }

    private void ShowPreset(SharedAiPreset? preset)
    {
        _current = preset;
        _name.Text = preset?.Name ?? "";
        _endpoint.Text = preset?.Endpoint ?? (_kind == SharedAiPresetKind.Text
            ? ClientSettingsDefaults.AiTextEndpoint : ClientSettingsDefaults.ImageModelEndpoint);
        _model.Text = preset?.Model ?? (_kind == SharedAiPresetKind.Text
            ? ClientSettingsDefaults.AiTextModel : ClientSettingsDefaults.ImageModelId);
        _key.Text = "";
        _key.Watermark = preset is null ? "填写 API Key" : "留空沿用已保存的 API Key";
        _timeout.Value = preset?.Timeout ?? 60;
        _ratio.SelectedItem = preset?.Ratio ?? "3:4";
        _message.Text = "";
    }

    private void Save()
    {
        try
        {
            var timeout = _timeout.Value ?? 0;
            if (_kind == SharedAiPresetKind.Text && timeout != decimal.Truncate(timeout))
                throw new ArgumentException("文本超时请填写整数秒。");
            var preset = SharedAiPresetStore.Save(new SharedAiPreset(
                _current?.Id ?? "", _kind, _name.Text ?? "", _endpoint.Text ?? "", _model.Text ?? "",
                (int)timeout, _ratio.SelectedItem as string ?? "3:4"), _key.Text);
            Refresh(preset.Id);
            _message.Text = "共享预设已保存。";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            _message.Text = ex is System.Security.Cryptography.CryptographicException
                ? "无法加密 API Key，请检查本机用户凭据。" : ex.Message;
        }
    }

    private static TextBox Input() => new() { Classes = { "configurationInput" } };

    private static Control Field(string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        return new StackPanel { Spacing = 5, Children = { new TextBlock { Text = label }, control } };
    }

    private static Grid WithButton(ComboBox input, Button button)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        input.Classes.Add("configurationInput");
        Grid.SetColumn(button, 1);
        grid.Children.Add(input);
        grid.Children.Add(button);
        return grid;
    }
}
