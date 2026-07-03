using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class AccountProfileEditor : UserControl
{
    private MainViewModel? _vm;

    public event EventHandler? LoginRequested;
    public event EventHandler? ReloginRequested;

    public AccountProfileEditor()
    {
        InitializeComponent();
        ExpectedPriceModeCombo.SelectionChanged += (_, _) => UpdateExpectedPriceModeVisibility();
        DataContextChanged += (_, _) => ReloadFromSelectedAccount();
    }

    public void Bind(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        ReloadFromSelectedAccount();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedAccount))
                ReloadFromSelectedAccount();
        };
    }

    private void ReloadFromSelectedAccount()
    {
        var profile = _vm?.SelectedAccount?.Model;
        if (profile is null)
        {
            ClearFields();
            LoginStatusText.Text = "尚未登录";
            return;
        }

        LoginStatusText.Text = AccountLoginStatusHelper.Describe(profile);
        NicknameBox.Text = profile.TiktokAccountNickname;
        LoginEmailBox.Text = profile.TiktokLoginEmail;
        PasswordBox.Text = profile.TiktokLoginPassword;
        StorageStateBox.Text = profile.TiktokStorageStatePath;
        SeriesUrlBox.Text = profile.TiktokSeriesUrl;
        WorkspaceBox.Text = !string.IsNullOrWhiteSpace(profile.TiktokUploadProfilePath)
            ? profile.TiktokUploadProfilePath
            : profile.LastWorkspace;
        DownloadWorkspaceBox.Text = profile.LastDownloadWorkspace;
        ExcelReportBox.Text = profile.TiktokExcelReportPath;

        SelectByTag(LoginBrowserModeCombo, profile.TiktokLoginBrowserMode, "embedded");
        CdpEndpointBox.Text = profile.TiktokFingerprintBrowserCdpEndpoint;
        FingerprintStartCommandBox.Text = profile.TiktokFingerprintStartCommand;

        ContractIdBox.Text = profile.TiktokContractId;
        SelectByTag(ContractModeCombo, profile.TiktokContractIdMode, "manual");
        SelectByTag(SubmitActionCombo, profile.TiktokSubmitAction, "draft");
        SelectByTag(PublishModeCombo, profile.TiktokPublishMode, "auto_after_review");
        SelectByTag(AudienceCombo, profile.TiktokTargetAudienceMode, "female");
        SelectByTag(SourceLanguageCombo, profile.TiktokSourceLanguage, "zh");
        SelectByTag(UploadStrategyCombo, profile.TiktokUploadStrategy, "classic");

        PaidEnabledBox.IsChecked = profile.TiktokPaidEnabled;
        PaidRatioEnabledBox.IsChecked = profile.TiktokPaidRatioEnabled;
        PaidRatioPercentBox.Value = (decimal)profile.TiktokPaidRatioPercent;
        AiDramaBox.IsChecked = profile.TiktokIsAiDrama;
        ConsignmentBox.IsChecked = profile.TiktokConsignmentEnabled;
        AnchorPromotionBox.IsChecked = profile.TiktokAnchorPromotionEnabled;
        SilenceValidationBox.IsChecked = profile.TiktokSilenceValidationEnabled;
        ProfilePreviewBox.Value = profile.TiktokProfilePreviewEpisodes;
        FreePreviewBox.Value = profile.TiktokFreePreviewEpisodes;
        GenreCountBox.Value = profile.TiktokGenreCount;
        UploadStallBox.Value = profile.TiktokUploadStallSeconds;
        ProjectConcurrencyBox.Value = profile.TiktokProjectConcurrency;
        UploadBatchSizeBox.Value = profile.TiktokUploadBatchSize;
        UploadBatchStallBox.Value = profile.TiktokUploadBatchStallSeconds;
        UploadBatchRetriesBox.Value = profile.TiktokUploadBatchMaxRetries;
        SilenceThresholdBox.Value = (decimal)profile.TiktokSilenceThresholdDb;

        SelectByTag(ExpectedPriceModeCombo, profile.TiktokExpectedFullPriceMode, "manual");
        ExpectedPriceOptionIndexBox.Value = profile.TiktokExpectedFullPriceOptionIndex;
        ExpectedPriceValueBox.Text = BuildExpectedPriceInputText(profile);
        ReloadExpectedPriceOptionsCombo(profile);
        UpdateExpectedPriceModeVisibility();

        ProxyEnabledBox.IsChecked = profile.TiktokProxyEnabled;
        SelectByTag(ProxyTypeCombo, profile.TiktokProxyType, "http");
        ProxyHostBox.Text = profile.TiktokProxyHost;
        ProxyPortBox.Value = profile.TiktokProxyPort;
        ProxyUsernameBox.Text = profile.TiktokProxyUsername;
        ProxyPasswordBox.Text = profile.TiktokProxyPassword;
        ProxyLabelBox.Text = profile.TiktokProxyLabel;
        StaticIpNoteBox.Text = profile.TiktokStaticIpNote;
    }

    private bool SaveToProfile()
    {
        var profile = _vm?.SelectedAccount?.Model;
        if (profile is null || _vm is null)
        {
            if (_vm is not null)
                _vm.StatusMessage = "请先在左侧选择一个账号";
            return false;
        }

        try
        {
            profile.TiktokAccountNickname = NicknameBox.Text?.Trim() ?? "";
            profile.TiktokLoginEmail = LoginEmailBox.Text?.Trim() ?? "";
            profile.TiktokLoginPassword = PasswordBox.Text ?? "";
            profile.TiktokStorageStatePath = StorageStateBox.Text?.Trim() ?? profile.TiktokStorageStatePath;
            profile.TiktokSeriesUrl = string.IsNullOrWhiteSpace(SeriesUrlBox.Text)
                ? TikTokUrls.DefaultSeriesDraftUrl
                : SeriesUrlBox.Text.Trim();

            var workspace = WorkspaceBox.Text?.Trim() ?? "";
            profile.TiktokUploadProfilePath = workspace;
            profile.LastWorkspace = workspace;
            profile.LastDownloadWorkspace = DownloadWorkspaceBox.Text?.Trim() ?? "";
            profile.TiktokExcelReportPath = ExcelReportBox.Text?.Trim() ?? "";

            profile.TiktokLoginBrowserMode = TagOf(LoginBrowserModeCombo, "embedded");
            profile.TiktokFingerprintBrowserCdpEndpoint = CdpEndpointBox.Text?.Trim() ?? "";
            profile.TiktokFingerprintStartCommand = FingerprintStartCommandBox.Text?.Trim() ?? "";

            profile.TiktokContractId = ContractIdBox.Text?.Trim() ?? "";
            profile.TiktokContractIdMode = TagOf(ContractModeCombo, "manual");
            profile.TiktokSubmitAction = TagOf(SubmitActionCombo, "draft");
            profile.TiktokPublishMode = TagOf(PublishModeCombo, "auto_after_review");
            profile.TiktokTargetAudienceMode = TagOf(AudienceCombo, "female");
            profile.TiktokSourceLanguage = TagOf(SourceLanguageCombo, "zh");
            profile.TiktokUploadStrategy = TagOf(UploadStrategyCombo, "classic");
            profile.TiktokPaidEnabled = PaidEnabledBox.IsChecked == true;
            profile.TiktokPaidRatioEnabled = PaidRatioEnabledBox.IsChecked == true;
            profile.TiktokPaidRatioPercent = (double)(PaidRatioPercentBox.Value ?? 0);
            profile.TiktokIsAiDrama = AiDramaBox.IsChecked == true;
            profile.TiktokConsignmentEnabled = ConsignmentBox.IsChecked == true;
            profile.TiktokAnchorPromotionEnabled = AnchorPromotionBox.IsChecked == true;
            profile.TiktokSilenceValidationEnabled = SilenceValidationBox.IsChecked == true;
            profile.TiktokProfilePreviewEpisodes = (int)(ProfilePreviewBox.Value ?? 1);
            profile.TiktokFreePreviewEpisodes = (int)(FreePreviewBox.Value ?? 1);
            profile.TiktokGenreCount = (int)(GenreCountBox.Value ?? 1);
            profile.TiktokUploadStallSeconds = (int)(UploadStallBox.Value ?? 180);
            profile.TiktokProjectConcurrency = (int)(ProjectConcurrencyBox.Value ?? 1);
            profile.TiktokUploadBatchSize = (int)(UploadBatchSizeBox.Value ?? 3);
            profile.TiktokUploadBatchStallSeconds = (int)(UploadBatchStallBox.Value ?? 75);
            profile.TiktokUploadBatchMaxRetries = (int)(UploadBatchRetriesBox.Value ?? 3);
            profile.TiktokSilenceThresholdDb = (double)(SilenceThresholdBox.Value ?? -45);
            profile.TiktokExpectedFullPriceMode = TagOf(ExpectedPriceModeCombo, "manual");
            profile.TiktokExpectedFullPriceOptionIndex = (int)(ExpectedPriceOptionIndexBox.Value ?? 1);
            var (priceValue, priceLabel) = NormalizeExpectedPriceInput(ExpectedPriceValueBox.Text);
            profile.TiktokExpectedFullPriceValue = priceValue;
            profile.TiktokExpectedFullPriceLabel = priceLabel;

            profile.TiktokProxyEnabled = ProxyEnabledBox.IsChecked == true;
            profile.TiktokProxyType = TagOf(ProxyTypeCombo, "http");
            profile.TiktokProxyHost = ProxyHostBox.Text?.Trim() ?? "";
            profile.TiktokProxyPort = (int)(ProxyPortBox.Value ?? 0);
            profile.TiktokProxyUsername = ProxyUsernameBox.Text?.Trim() ?? "";
            profile.TiktokProxyPassword = ProxyPasswordBox.Text ?? "";
            profile.TiktokProxyLabel = ProxyLabelBox.Text?.Trim() ?? "";
            profile.TiktokStaticIpNote = StaticIpNoteBox.Text?.Trim() ?? "";

            _vm.SaveAccountProfile(profile);
            _vm.SelectedAccount?.RefreshFromModel();
            _vm.RefreshFilteredAccounts();
            _vm.StatusMessage = $"已保存账号「{profile.DisplayName}」配置";
            ReloadFromSelectedAccount();
            return true;
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"保存账号配置失败：{ex.Message}";
            _vm.AppendLog($"保存账号配置失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SaveToProfile();
    }

    private void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;
        if (_vm is not null)
            _vm.StatusMessage = "已保存账号配置，正在启动登录…";
        LoginRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReloginClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;
        if (_vm is not null)
            _vm.StatusMessage = "已保存账号配置，正在重新登录…";
        ReloginRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClearFields()
    {
        NicknameBox.Text = "";
        LoginEmailBox.Text = "";
        PasswordBox.Text = "";
        StorageStateBox.Text = "";
        SeriesUrlBox.Text = TikTokUrls.DefaultSeriesDraftUrl;
        WorkspaceBox.Text = "";
        DownloadWorkspaceBox.Text = "";
        ExcelReportBox.Text = "";
        CdpEndpointBox.Text = "";
        FingerprintStartCommandBox.Text = "";
        ContractIdBox.Text = "";
        ExpectedPriceValueBox.Text = "";
        ExpectedPriceOptionsCombo.Items.Clear();
        ProxyHostBox.Text = "";
        ProxyUsernameBox.Text = "";
        ProxyPasswordBox.Text = "";
        ProxyLabelBox.Text = "";
        StaticIpNoteBox.Text = "";
    }

    private async void OnBrowseAuthPathClick(object? sender, RoutedEventArgs e) =>
        await PickFileAsync(StorageStateBox, "选择登录态 JSON 文件", ["json"]);

    private async void OnBrowseWorkspaceClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAsync(WorkspaceBox, "选择上传工作目录");

    private async void OnBrowseDownloadWorkspaceClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAsync(DownloadWorkspaceBox, "选择下载工作目录");

    private async void OnBrowseExcelReportClick(object? sender, RoutedEventArgs e) =>
        await PickFileAsync(ExcelReportBox, "选择 Excel 报表文件", ["xlsx", "xls"]);

    private async void OnSyncExpectedPriceClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedAccount?.Model is not { } profile) return;
        SaveToProfile();
        try
        {
            await _vm.SyncExpectedPriceOptionsAsync(profile);
            ReloadFromSelectedAccount();
        }
        catch
        {
            // 状态已在 ViewModel 中更新
        }
    }

    private void ReloadExpectedPriceOptionsCombo(TikTokAccountProfile profile)
    {
        ExpectedPriceOptionsCombo.Items.Clear();
        foreach (var option in ExpectedFullPriceOptionsJson.Parse(profile.TiktokExpectedFullPriceOptionsJson))
        {
            ExpectedPriceOptionsCombo.Items.Add(new ComboBoxItem
            {
                Content = option.Label,
                Tag = option.Value,
            });
        }

        if (!string.IsNullOrWhiteSpace(profile.TiktokExpectedFullPriceValue))
        {
            foreach (var item in ExpectedPriceOptionsCombo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, profile.TiktokExpectedFullPriceValue, StringComparison.Ordinal))
                {
                    ExpectedPriceOptionsCombo.SelectedItem = item;
                    break;
                }
            }
        }

        ExpectedPriceOptionsCombo.SelectionChanged -= OnExpectedPriceOptionSelected;
        ExpectedPriceOptionsCombo.SelectionChanged += OnExpectedPriceOptionSelected;
    }

    private void OnExpectedPriceOptionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ExpectedPriceOptionsCombo.SelectedItem is not ComboBoxItem item) return;
        var value = item.Tag as string ?? "";
        var label = item.Content?.ToString() ?? "";
        ExpectedPriceValueBox.Text = BuildExpectedPriceInputText(value, label);
    }

    private void UpdateExpectedPriceModeVisibility()
    {
        var mode = TagOf(ExpectedPriceModeCombo, "manual");
        ExpectedPriceValueBox.IsVisible = mode == "manual";
        ExpectedPriceOptionsCombo.IsVisible = mode == "manual";
        ExpectedPriceOptionIndexBox.IsVisible = mode == "option_index";
    }

    private static string BuildExpectedPriceInputText(TikTokAccountProfile profile) =>
        BuildExpectedPriceInputText(profile.TiktokExpectedFullPriceValue, profile.TiktokExpectedFullPriceLabel);

    private static string BuildExpectedPriceInputText(string value, string label)
    {
        value = (value ?? "").Trim();
        label = (label ?? "").Trim();
        if (!string.IsNullOrEmpty(label)) return label;
        return value;
    }

    private static (string Value, string Label) NormalizeExpectedPriceInput(string? text)
    {
        var raw = (text ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) return ("", "");

        var totalMatch = Regex.Match(raw, @"\$?(\d+(?:\.\d+)?)");
        var value = totalMatch.Success ? totalMatch.Groups[1].Value : raw;
        var label = raw.Contains("每集", StringComparison.Ordinal) || raw.Contains("/EP", StringComparison.OrdinalIgnoreCase)
            ? raw
            : (raw.StartsWith('$') ? raw : $"${value}");
        return (value, label);
    }

    private async Task PickFolderAsync(TextBox target, string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;
        target.Text = folder.Path.LocalPath;
    }

    private async Task PickFileAsync(TextBox target, string title, IReadOnlyList<string> extensions)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(title)
                {
                    Patterns = extensions.Select(ext => $"*.{ext}").ToList(),
                },
            },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        target.Text = file.Path.LocalPath;
    }

    private static void SelectByTag(ComboBox combo, string? tag, string fallback)
    {
        var key = string.IsNullOrWhiteSpace(tag) ? fallback : tag.Trim();
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = ci;
                return;
            }
        }
        if (combo.ItemCount > 0) combo.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
}
