using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.Services;
using System.Diagnostics;

namespace ShortDrama.Desktop.Views;

public partial class MaterialClipAsrConfigWindow : Window
{
    private readonly GlobalSettingsService _globalSettingsService;

    public MaterialClipAsrConfigWindow(GlobalSettingsService globalSettingsService)
    {
        _globalSettingsService = globalSettingsService;
        InitializeComponent();

        ProviderComboBox.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("volcengine_stt", "字节语音识别大模型极速版"),
            new KeyValuePair<string, string>("doubao_subtitle", "豆包字幕识别")
        };
        ProviderComboBox.DisplayMemberBinding = new Avalonia.Data.Binding("Value");
        ProviderComboBox.SelectionChanged += (_, _) => LoadProviderValues();

        LanguageComboBox.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("zh-CN", "中文"),
            new KeyValuePair<string, string>("en", "英文"),
            new KeyValuePair<string, string>("ja", "日文"),
            new KeyValuePair<string, string>("ko", "韩文")
        };
        LanguageComboBox.DisplayMemberBinding = new Avalonia.Data.Binding("Value");

        SaveButton.Click += SaveButton_Click;
        CancelButton.Click += (_, _) => Close(false);
        TutorialButton.Click += TutorialButton_Click;

        LoadValues();
    }

    private void LoadValues()
    {
        var settings = _globalSettingsService.Load();
        var provider = string.IsNullOrWhiteSpace(settings.MaterialClipAsrProvider)
            ? "volcengine_stt"
            : settings.MaterialClipAsrProvider;
        ProviderComboBox.SelectedItem = ((IEnumerable<KeyValuePair<string, string>>)ProviderComboBox.ItemsSource!)
            .FirstOrDefault(item => string.Equals(item.Key, provider, StringComparison.OrdinalIgnoreCase));

        var language = string.IsNullOrWhiteSpace(settings.MaterialClipAsrLanguage)
            ? "zh-CN"
            : settings.MaterialClipAsrLanguage;
        LanguageComboBox.SelectedItem = ((IEnumerable<KeyValuePair<string, string>>)LanguageComboBox.ItemsSource!)
            .FirstOrDefault(item => string.Equals(item.Key, language, StringComparison.OrdinalIgnoreCase));

        LoadProviderValues();
    }

    private void LoadProviderValues()
    {
        var settings = _globalSettingsService.Load();
        var provider = (ProviderComboBox.SelectedItem as KeyValuePair<string, string>?)?.Key ?? "volcengine_stt";
        if (string.Equals(provider, "doubao_subtitle", StringComparison.OrdinalIgnoreCase))
        {
            AppIdTextBox.Text = settings.MaterialClipDoubaoAppId;
            AccessTokenTextBox.Text = settings.MaterialClipDoubaoAccessToken;
            return;
        }

        AppIdTextBox.Text = settings.MaterialClipVolcengineAppId;
        AccessTokenTextBox.Text = settings.MaterialClipVolcengineAccessToken;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var current = _globalSettingsService.Load();
        var provider = (ProviderComboBox.SelectedItem as KeyValuePair<string, string>?)?.Key ?? "volcengine_stt";
        var language = (LanguageComboBox.SelectedItem as KeyValuePair<string, string>?)?.Key ?? "zh-CN";
        var updated = string.Equals(provider, "doubao_subtitle", StringComparison.OrdinalIgnoreCase)
            ? current with
            {
                MaterialClipAsrProvider = provider,
                MaterialClipAsrLanguage = language,
                MaterialClipDoubaoAppId = AppIdTextBox.Text?.Trim() ?? string.Empty,
                MaterialClipDoubaoAccessToken = AccessTokenTextBox.Text?.Trim() ?? string.Empty
            }
            : current with
            {
                MaterialClipAsrProvider = provider,
                MaterialClipAsrLanguage = language,
                MaterialClipVolcengineAppId = AppIdTextBox.Text?.Trim() ?? string.Empty,
                MaterialClipVolcengineAccessToken = AccessTokenTextBox.Text?.Trim() ?? string.Empty
            };
        _globalSettingsService.Save(updated);
        Close(true);
    }

    private static void TutorialButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://pyvideotrans.com/zijierecognmodel",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
