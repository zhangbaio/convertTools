using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.Services;

namespace ShortDrama.Desktop.Views;

/// <summary>系统服务视图。当前收口「ASR配置」子标签（三引擎：火山在线 / 本地 SenseVoice / 混合），
/// 配置项迁移自 Python「系统服务 → ASR配置」。测试/下载属运行时能力，待 .NET ASR 运行时接入后再补。</summary>
public partial class SystemServiceView : UserControl
{
    private GlobalSettingsService? _settingsService;
    private GlobalConfigSnapshot? _snapshot;
    private bool _loaded;

    public SystemServiceView()
    {
        InitializeComponent();

        EngineCombo.SelectionChanged += (_, _) => UpdateFieldStates();
        SaveButton.Click += OnSave;
        BrowseModelDirButton.Click += OnBrowseModelDir;
        VolcTutorialButton.Click += (_, _) => OpenUrl("https://pyvideotrans.com/zijierecognmodel");
        VolcTestButton.Click += (_, _) => VolcTestResultLabel.Text = "ASR 运行时尚未接入 .NET（配置已可保存，联调后补测试）";
        LocalTestButton.Click += (_, _) => LocalTestResultLabel.Text = "ASR 运行时尚未接入 .NET（配置已可保存，联调后补测试）";
        DownloadModelButton.Click += (_, _) => LocalTestResultLabel.Text = "模型下载能力待 .NET ASR 运行时接入后再补，可先手动放到 models/ 目录";

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        if (Application.Current is not App app) return;
        _settingsService = app.Services.GetRequiredService<GlobalSettingsService>();
        LoadValues();
        _loaded = true;
    }

    private void LoadValues()
    {
        var s = _settingsService?.Load();
        _snapshot = s;
        if (s is null) return;

        SelectByTag(EngineCombo, string.IsNullOrWhiteSpace(s.MaterialClipAsrEngine) ? "volcengine" : s.MaterialClipAsrEngine);
        SelectByTag(LanguageCombo, string.IsNullOrWhiteSpace(s.MaterialClipAsrLanguage) ? "zh-CN" : s.MaterialClipAsrLanguage);
        VolcAppIdBox.Text = s.MaterialClipVolcengineAppId;
        VolcTokenBox.Text = s.MaterialClipVolcengineAccessToken;
        SelectByTag(LocalModelCombo, string.IsNullOrWhiteSpace(s.MaterialClipAsrLocalModel) ? "sensevoice" : s.MaterialClipAsrLocalModel);
        LocalModelDirBox.Text = s.MaterialClipAsrLocalModelDir;
        LocalVadBox.Text = s.MaterialClipAsrLocalVadPath;
        ItnBox.IsChecked = s.MaterialClipAsrLocalUseItn;
        HybridThresholdBox.Value = double.TryParse(s.MaterialClipAsrHybridMinCharsPerSec, NumberStyles.Float, CultureInfo.InvariantCulture, out var thr)
            ? (decimal)thr
            : 1.0m;

        UpdateFieldStates();
    }

    // 按所选引擎启停在线/本地字段：火山→在线；本地→本地；混合→两者+复核阈值。
    private void UpdateFieldStates()
    {
        var engine = TagOf(EngineCombo, "volcengine");
        var useOnline = engine is "volcengine" or "hybrid";
        var useLocal = engine is "local" or "hybrid";

        VolcAppIdBox.IsEnabled = useOnline;
        VolcTokenBox.IsEnabled = useOnline;
        VolcTestButton.IsEnabled = useOnline;

        LocalModelCombo.IsEnabled = useLocal;
        LocalModelDirBox.IsEnabled = useLocal;
        BrowseModelDirButton.IsEnabled = useLocal;
        DownloadModelButton.IsEnabled = useLocal;
        LocalVadBox.IsEnabled = useLocal;
        ItnBox.IsEnabled = useLocal;
        LocalTestButton.IsEnabled = useLocal;

        HybridThresholdBox.IsEnabled = engine == "hybrid";
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_settingsService is null || _snapshot is null) return;

        var updated = _snapshot with
        {
            MaterialClipAsrEngine = TagOf(EngineCombo, "volcengine"),
            MaterialClipAsrLanguage = TagOf(LanguageCombo, "zh-CN"),
            MaterialClipVolcengineAppId = VolcAppIdBox.Text?.Trim() ?? string.Empty,
            MaterialClipVolcengineAccessToken = VolcTokenBox.Text?.Trim() ?? string.Empty,
            MaterialClipAsrLocalModel = TagOf(LocalModelCombo, "sensevoice"),
            MaterialClipAsrLocalModelDir = LocalModelDirBox.Text?.Trim() ?? string.Empty,
            MaterialClipAsrLocalVadPath = LocalVadBox.Text?.Trim() ?? string.Empty,
            MaterialClipAsrLocalUseItn = ItnBox.IsChecked == true,
            MaterialClipAsrHybridMinCharsPerSec = ((double)(HybridThresholdBox.Value ?? 1.0m)).ToString("0.###", CultureInfo.InvariantCulture),
        };
        _settingsService.Save(updated);
        _snapshot = _settingsService.Load(); // 回读，拿到规范化后的快照
        SaveHintLabel.Text = "已保存";
    }

    private async void OnBrowseModelDir(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not TopLevel topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择本地 ASR 模型目录",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null) LocalModelDirBox.Text = folder.Path.LocalPath;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* 打开浏览器失败忽略 */ }
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag) { combo.SelectedItem = ci; return; }
        if (combo.ItemCount > 0) combo.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
}
