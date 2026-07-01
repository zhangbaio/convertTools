using Avalonia.Controls;
using Avalonia.Interactivity;
using ConvertTools.App.Services;

namespace ConvertTools.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var c = AppConfig.Current;
        FfmpegBox.Text = c.FfmpegPath;
        AiEndpointBox.Text = c.AiEndpoint;
        AiKeyBox.Text = c.AiApiKey;
        AiModelBox.Text = c.AiModel;
        FinalActionCombo.SelectedIndex = c.DefaultFinalAction switch
        {
            "draft" => 1,
            "publish" => 2,
            _ => 0,
        };
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var config = new AppConfig
        {
            FfmpegPath = string.IsNullOrWhiteSpace(FfmpegBox.Text) ? "ffmpeg" : FfmpegBox.Text!.Trim(),
            AiEndpoint = AiEndpointBox.Text?.Trim() ?? "",
            AiApiKey = AiKeyBox.Text?.Trim() ?? "",
            AiModel = AiModelBox.Text?.Trim() ?? "",
            DefaultFinalAction = (FinalActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none",
        };
        config.Save();
        SaveStatus.Text = "已保存 ✓";
    }
}
