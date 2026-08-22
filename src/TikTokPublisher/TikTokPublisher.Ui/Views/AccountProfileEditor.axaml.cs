using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class AccountProfileEditor : UserControl
{
    private MainViewModel? _vm;
    private bool _isReloadingFromSelectedAccount;

    public event EventHandler? LoginRequested;
    public event EventHandler? ReloginRequested;
    public event EventHandler? LogoutRequested;

    public AccountProfileEditor()
    {
        InitializeComponent();
        ExpectedPriceModeCombo.SelectionChanged += (_, _) => UpdateExpectedPriceModeVisibility();
        DataContextChanged += (_, _) => ReloadFromSelectedAccount();
    }

    public void Bind(MainViewModel vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.AccountProfileNetworkChanged -= OnAccountProfileChanged;
        }

        _vm = vm;
        DataContext = vm;
        ReloadFromSelectedAccount();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.AccountProfileNetworkChanged += OnAccountProfileChanged;
    }

    public void RefreshSelectedAccount() => ReloadFromSelectedAccount();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAccount))
            ReloadFromSelectedAccount();
        else if (e.PropertyName == nameof(MainViewModel.WorkspacePath))
            SyncWorkspaceBoxFromSelectedAccount();
    }

    private void OnAccountProfileChanged(TikTokAccountProfile profile)
    {
        if (_vm?.SelectedAccount?.Id == profile.Id)
            ReloadFromSelectedAccount();
    }

    private void ReloadFromSelectedAccount()
    {
        if (_isReloadingFromSelectedAccount)
            return;

        _isReloadingFromSelectedAccount = true;
        try
        {
            ReloadFromSelectedAccountCore();
        }
        finally
        {
            _isReloadingFromSelectedAccount = false;
        }
    }

    private void ReloadFromSelectedAccountCore()
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
        ArchiveRootBox.Text = profile.TiktokArchiveRootDir;
        DownloadWorkspaceBox.Text = profile.LastDownloadWorkspace;
        ExcelReportBox.Text = profile.TiktokExcelReportPath;
        DeleteVideosOnArchiveBox.IsChecked = profile.TiktokDeleteVideosOnArchive;

        SelectByTag(LoginBrowserModeCombo, profile.TiktokLoginBrowserMode, "embedded");
        CdpEndpointBox.Text = profile.TiktokExternalBrowserCdpEndpoint;

        ContractIdBox.Text = profile.TiktokContractId;
        SelectByTag(ContractModeCombo, profile.TiktokContractIdMode, "manual");
        var submitAction = NormalizeSubmitAction(profile.TiktokSubmitAction, profile.TiktokSubmitEnabled);
        SelectByTag(SubmitActionCombo, submitAction, "submit");
        SubmitEnabledBox.IsChecked = string.Equals(submitAction, "submit", StringComparison.Ordinal);
        SelectByTag(UploadBrowserModeCombo, profile.TiktokUploadBrowserMode, "embedded");
        PlaywrightHeadlessBox.IsChecked = profile.TiktokPlaywrightUploadHeadless;
        SelectByTag(PublishModeCombo, profile.TiktokPublishMode, "auto_after_review");
        SelectByTag(AudienceCombo, profile.TiktokTargetAudienceMode, "ai_recommend");
        SelectByTag(SourceLanguageCombo, profile.TiktokSourceLanguage, "zh");
        SelectByTag(UploadStrategyCombo, profile.TiktokUploadStrategy, "classic");

        PaidEnabledBox.IsChecked = profile.TiktokPaidEnabled;
        PaidRatioEnabledBox.IsChecked = profile.TiktokPaidRatioEnabled;
        PaidRatioPercentBox.Value = (decimal)(profile.TiktokPaidRatioPercent > 0
            ? profile.TiktokPaidRatioPercent
            : 20);
        AiDramaBox.IsChecked = profile.TiktokIsAiDrama;
        SelectByTag(ContentCreationTypeCombo, profile.TiktokContentCreationType, "original");
        OriginalRightsHolderBox.IsChecked = profile.TiktokIsOriginalRightsHolder;
        SelectByTag(ContentOriginalityCombo, profile.TiktokContentOriginalityType, "original");
        CopyrightMaterials.Load(
            profile.TiktokCopyrightMaterialTypes,
            profile.TiktokUploadAiScriptOutlineWithScreenshots,
            profile.TiktokUploadSourceInfoRoleSceneScreenshot);
        ProofDeclarantCompanyNameBox.Text = profile.TiktokProofDeclarantCompanyName;
        TimestampApplicantNameBox.Text = profile.TiktokTimestampApplicantName;
        ProofSealPathBox.Text = profile.TiktokProofSealPath;
        ProofCopyrightCompanyNameBox.Text = profile.TiktokProofCopyrightCompanyName;
        EpisodeScriptEpisodeCountBox.Value = Math.Clamp(
            profile.TiktokEpisodeScriptEpisodeCount > 0
                ? profile.TiktokEpisodeScriptEpisodeCount
                : TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount,
            1,
            120);
        AiScriptOutlineEpisodeCountBox.Value = Math.Clamp(
            profile.TiktokAiScriptOutlineEpisodeCount > 0
                ? profile.TiktokAiScriptOutlineEpisodeCount
                : TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount,
            1,
            120);
        RoleVectorCharacterCountBox.Value = Math.Clamp(
            profile.TiktokRoleVectorCharacterCount > 0
                ? profile.TiktokRoleVectorCharacterCount
                : TikTokAccountProfile.DefaultRoleVectorCharacterCount,
            2,
            6);
        RoleVectorMinimumCharacterCountBox.Value = Math.Clamp(
            profile.TiktokRoleVectorMinimumCharacterCount > 0
                ? profile.TiktokRoleVectorMinimumCharacterCount
                : TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount,
            2,
            (int)(RoleVectorCharacterCountBox.Value ?? TikTokAccountProfile.DefaultRoleVectorCharacterCount));
        AiRewriteSynopsisBox.IsChecked = profile.TiktokAiRewriteSynopsis;
        ConsignmentBox.IsChecked = profile.TiktokConsignmentEnabled;
        ZeroCostAdsBox.IsChecked = profile.TiktokZeroCostAdsEnabled;
        DayZeroRoiBox.Value = (decimal)TikTokPublishOptions.NormalizeDayZeroRoi(profile.TiktokDayZeroRoi);
        AnchorPromotionBox.IsChecked = profile.TiktokAnchorPromotionEnabled;
        SilenceValidationBox.IsChecked = profile.TiktokSilenceValidationEnabled;
        ProfilePreviewBox.Value = profile.TiktokProfilePreviewEpisodes > 0 ? profile.TiktokProfilePreviewEpisodes : 3;
        FreePreviewBox.Value = profile.TiktokFreePreviewEpisodes > 0 ? profile.TiktokFreePreviewEpisodes : 3;
        GenreCountBox.Value = TikTokPublishOptions.NormalizeGenreCount(profile.TiktokGenreCount);
        UploadStallBox.Value = profile.TiktokUploadStallSeconds;
        ProjectConcurrencyBox.Value = profile.TiktokProjectConcurrency;
        UploadBatchSizeBox.Value = profile.TiktokUploadBatchSize;
        UploadBatchStallBox.Value = profile.TiktokUploadBatchStallSeconds;
        UploadBatchRetriesBox.Value = profile.TiktokUploadBatchMaxRetries;
        SilenceThresholdBox.Value = (decimal)profile.TiktokSilenceThresholdDb;
        MaxContinuousSilenceSecondsBox.Value = profile.TiktokMaxContinuousSilenceSeconds;

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

    private void SyncWorkspaceBoxFromSelectedAccount()
    {
        var profile = _vm?.SelectedAccount?.Model;
        var workspace = profile is null
            ? ""
            : !string.IsNullOrWhiteSpace(profile.TiktokUploadProfilePath)
                ? profile.TiktokUploadProfilePath
                : profile.LastWorkspace;

        if (!string.Equals(WorkspaceBox.Text ?? "", workspace, StringComparison.Ordinal))
            WorkspaceBox.Text = workspace;
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
            profile.TiktokArchiveRootDir = ArchiveRootBox.Text?.Trim() ?? "";
            profile.TiktokArchiveRootConfigMigrated = true;
            profile.LastDownloadWorkspace = DownloadWorkspaceBox.Text?.Trim() ?? "";
            profile.TiktokExcelReportPath = ExcelReportBox.Text?.Trim() ?? "";
            profile.TiktokDeleteVideosOnArchive = DeleteVideosOnArchiveBox.IsChecked == true;
            profile.TiktokDeleteVideosOnArchiveConfigured = true;

            profile.TiktokLoginBrowserMode = TagOf(LoginBrowserModeCombo, "embedded");
            profile.TiktokExternalBrowserCdpEndpoint = CdpEndpointBox.Text?.Trim() ?? "";

            profile.TiktokContractId = ContractIdBox.Text?.Trim() ?? "";
            profile.TiktokContractIdMode = TagOf(ContractModeCombo, "manual");
            profile.TiktokSubmitAction = NormalizeSubmitAction(TagOf(SubmitActionCombo, "submit"), SubmitEnabledBox.IsChecked == true);
            profile.TiktokSubmitEnabled = string.Equals(profile.TiktokSubmitAction, "submit", StringComparison.Ordinal);
            profile.TiktokUploadBrowserMode = TagOf(UploadBrowserModeCombo, "embedded");
            profile.TiktokPlaywrightUploadHeadless = PlaywrightHeadlessBox.IsChecked == true;
            profile.TiktokPublishMode = TagOf(PublishModeCombo, "auto_after_review");
            profile.TiktokTargetAudienceMode = TagOf(AudienceCombo, "ai_recommend");
            profile.TiktokSourceLanguage = TagOf(SourceLanguageCombo, "zh");
            profile.TiktokUploadStrategy = TagOf(UploadStrategyCombo, "classic");
            profile.TiktokPaidEnabled = PaidEnabledBox.IsChecked == true;
            profile.TiktokPaidRatioEnabled = PaidRatioEnabledBox.IsChecked == true;
            profile.TiktokPaidRatioPercent = (double)(PaidRatioPercentBox.Value ?? 0);
            profile.TiktokIsAiDrama = AiDramaBox.IsChecked == true;
            profile.TiktokContentCreationType = TagOf(ContentCreationTypeCombo, "original");
            profile.TiktokIsOriginalRightsHolder = OriginalRightsHolderBox.IsChecked == true;
            profile.TiktokContentOriginalityType = TagOf(ContentOriginalityCombo, "original");
            profile.TiktokCopyrightMaterialTypes = CopyrightMaterials.GetSelectedMaterialTypes();
            profile.TiktokUploadAiScriptOutlineWithScreenshots = CopyrightMaterials.UploadAiScriptOutlineWithScreenshots;
            profile.TiktokUploadSourceInfoRoleSceneScreenshot = CopyrightMaterials.UploadSourceInfoRoleSceneScreenshot;
            profile.TiktokProofDeclarantCompanyName = ProofDeclarantCompanyNameBox.Text?.Trim() ?? "";
            profile.TiktokTimestampApplicantName = TimestampApplicantNameBox.Text?.Trim() ?? "";
            profile.TiktokProofSealPath = ProofSealPathBox.Text?.Trim() ?? "";
            profile.TiktokProofCopyrightCompanyName = ProofCopyrightCompanyNameBox.Text?.Trim() ?? "";
            profile.TiktokEpisodeScriptEpisodeCount = Math.Clamp(
                (int)(EpisodeScriptEpisodeCountBox.Value ?? TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount),
                1,
                120);
            profile.TiktokAiScriptOutlineEpisodeCount = Math.Clamp(
                (int)(AiScriptOutlineEpisodeCountBox.Value ?? TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount),
                1,
                120);
            profile.TiktokRoleVectorCharacterCount = Math.Clamp(
                (int)(RoleVectorCharacterCountBox.Value ?? TikTokAccountProfile.DefaultRoleVectorCharacterCount),
                2,
                6);
            profile.TiktokRoleVectorMinimumCharacterCount = Math.Clamp(
                (int)(RoleVectorMinimumCharacterCountBox.Value ??
                      TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount),
                2,
                profile.TiktokRoleVectorCharacterCount);
            profile.TiktokProofAccountConfigMigrated = true;
            profile.TiktokAiRewriteSynopsis = AiRewriteSynopsisBox.IsChecked == true;
            profile.TiktokConsignmentEnabled = ConsignmentBox.IsChecked == true;
            profile.TiktokZeroCostAdsEnabled = ZeroCostAdsBox.IsChecked == true;
            profile.TiktokDayZeroRoi = TikTokPublishOptions.NormalizeDayZeroRoi(
                (double)(DayZeroRoiBox.Value ?? (decimal)TikTokPublishOptions.DefaultDayZeroRoi));
            profile.TiktokAnchorPromotionEnabled = AnchorPromotionBox.IsChecked == true;
            profile.TiktokSilenceValidationEnabled = SilenceValidationBox.IsChecked == true;
            profile.TiktokProfilePreviewEpisodes = (int)(ProfilePreviewBox.Value ?? 3);
            profile.TiktokFreePreviewEpisodes = (int)(FreePreviewBox.Value ?? 3);
            profile.TiktokGenreCount = TikTokPublishOptions.NormalizeGenreCount((int)(GenreCountBox.Value ?? TikTokPublishOptions.DefaultGenreCount));
            profile.TiktokUploadStallSeconds = (int)(UploadStallBox.Value ?? 180);
            profile.TiktokProjectConcurrency = (int)(ProjectConcurrencyBox.Value ?? 4);
            profile.TiktokUploadBatchSize = (int)(UploadBatchSizeBox.Value ?? 3);
            profile.TiktokUploadBatchStallSeconds = (int)(UploadBatchStallBox.Value ?? 75);
            profile.TiktokUploadBatchMaxRetries = (int)(UploadBatchRetriesBox.Value ?? 3);
            profile.TiktokSilenceThresholdDb = (double)(SilenceThresholdBox.Value ?? -45);
            profile.TiktokMaxContinuousSilenceSeconds = (int)(MaxContinuousSilenceSecondsBox.Value ?? 20);
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

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        await InfoDialog.ShowSaveSuccessAsync(owner, "账号配置已保存成功。");
    }

    private void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;
        if (_vm is not null)
            _vm.StatusMessage = "已保存账号配置，正在打开内置浏览器…";
        LoginRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReloginClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;
        if (_vm is not null)
            _vm.StatusMessage = "已保存账号配置，正在重新打开内置浏览器…";
        ReloginRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnTestProxyClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        var enabled = ProxyEnabledBox.IsChecked == true;
        var type = TagOf(ProxyTypeCombo, "http");
        var host = ProxyHostBox.Text?.Trim() ?? "";
        var port = (int)(ProxyPortBox.Value ?? 0);
        var user = ProxyUsernameBox.Text?.Trim() ?? "";
        var pass = ProxyPasswordBox.Text ?? "";

        TestProxyButton.IsEnabled = false;
        ProxyTestResultText.Foreground = Brushes.Gray;
        ProxyTestResultText.Text = "正在测试，请稍候…";
        try
        {
            var (ok, message) = await TestProxyAsync(enabled, type, host, port, user, pass);
            ProxyTestResultText.Foreground = ok ? Brushes.SeaGreen : Brushes.IndianRed;
            ProxyTestResultText.Text = message;
        }
        catch (Exception ex)
        {
            ProxyTestResultText.Foreground = Brushes.IndianRed;
            ProxyTestResultText.Text = $"测试失败：{ex.Message}";
        }
        finally
        {
            TestProxyButton.IsEnabled = true;
        }
    }

    private static async Task<(bool Ok, string Message)> TestProxyAsync(
        bool enabled, string type, string host, int port, string user, string pass)
    {
        using var handler = new HttpClientHandler();
        string modeDesc;
        if (enabled)
        {
            if (string.IsNullOrEmpty(host))
                return (false, "已勾选「启用账号代理」，但未填写代理主机。");

            var scheme = TikTokProxyHelper.NormalizeProxyType(type);
            var server = host.Contains("://", StringComparison.Ordinal)
                ? host
                : port > 0 ? $"{scheme}://{host}:{port}" : $"{scheme}://{host}";

            var proxy = new WebProxy(server);
            if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass))
                proxy.Credentials = new NetworkCredential(user, pass);
            handler.Proxy = proxy;
            handler.UseProxy = true;
            modeDesc = $"经代理 {server}";
        }
        else
        {
            handler.UseProxy = false;
            modeDesc = "直连（未启用代理）";
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 TikTokPublisher");

        var result = await LookupOutboundIpAsync(client);
        var ip = string.IsNullOrWhiteSpace(result.Ip) ? "未知" : result.Ip;
        var location = string.IsNullOrWhiteSpace(result.Location) ? "未知" : ToChineseIpLocation(result.Location);
        var org = string.IsNullOrWhiteSpace(result.Org) ? "未知" : ToChineseIpOrg(result.Org);
        var text = $"✓ 出口 IP：{ip}（归属地：{location}）\n"
                 + $"运营商：{org}\n"
                 + $"方式：{modeDesc}";
        return (true, text);
    }

    private static async Task<IpLookupResult> LookupOutboundIpAsync(HttpClient client)
    {
        Exception? lastError = null;
        var results = new List<IpLookupResult>();

        foreach (var url in new[] { "https://ip9.com.cn/get", "https://ipinfo.io/json", "https://ipwho.is/" })
        {
            try
            {
                using var resp = await client.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var result = url.Contains("ip9.com.cn", StringComparison.OrdinalIgnoreCase)
                    ? ParseIp9(root)
                    : url.Contains("ipwho.is", StringComparison.OrdinalIgnoreCase)
                        ? ParseIpWhoIs(root)
                        : ParseIpInfo(root);
                if (!string.IsNullOrWhiteSpace(result.Ip))
                    results.Add(result);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        var primary = results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.Ip));
        if (primary is not null)
        {
            var matchingResults = results
                .Where(result => string.Equals(result.Ip, primary.Ip, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchingResults.Count == 0)
                matchingResults = results;

            var location = FirstNonEmpty(matchingResults.Select(result => result.Location).ToArray());
            var org = FirstNonEmpty(
                matchingResults.Where(result => LooksLikeCarrierOrg(result.Org)).Select(result => result.Org)
                    .Concat(matchingResults.Select(result => result.Org))
                    .ToArray());
            return new IpLookupResult(primary.Ip, location, org);
        }

        throw new InvalidOperationException($"无法获取出口 IP 信息：{lastError?.Message ?? "未知错误"}");
    }

    private static bool LooksLikeCarrierOrg(string value)
    {
        var normalized = Regex.Replace(value, @"[\s._-]+", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        return normalized.Contains("chinatelecom")
            || normalized.Contains("telecom")
            || normalized.Contains("chinaunicom")
            || normalized.Contains("unicom")
            || normalized.Contains("chinamobile")
            || normalized.Contains("cmcc")
            || normalized.Contains("cmi");
    }

    private static IpLookupResult ParseIp9(JsonElement root)
    {
        if (root.TryGetProperty("ret", out var retValue) &&
            retValue.ValueKind == JsonValueKind.Number &&
            retValue.TryGetInt32(out var ret) &&
            ret != 200)
        {
            return new IpLookupResult("", "", "");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new IpLookupResult("", "", "");

        var ip = GetJsonString(data, "ip");
        var country = GetJsonString(data, "country");
        var province = GetJsonString(data, "prov");
        var city = GetJsonString(data, "city");
        var area = GetJsonString(data, "area");
        var isp = GetJsonString(data, "isp");

        return new IpLookupResult(ip, JoinLocation(country, province, city, area), isp);
    }

    private static IpLookupResult ParseIpWhoIs(JsonElement root)
    {
        var success = !root.TryGetProperty("success", out var successValue)
            || successValue.ValueKind != JsonValueKind.False;
        if (!success)
            return new IpLookupResult("", "", "");

        var ip = GetJsonString(root, "ip");
        var country = GetJsonString(root, "country");
        var region = GetJsonString(root, "region");
        var city = GetJsonString(root, "city");
        var org = GetJsonString(root, "org");
        var isp = GetJsonString(root, "isp");

        if (root.TryGetProperty("connection", out var connection))
        {
            org = FirstNonEmpty(GetJsonString(connection, "org"), org);
            isp = FirstNonEmpty(GetJsonString(connection, "isp"), isp);
        }

        return new IpLookupResult(ip, JoinLocation(country, region, city), FirstNonEmpty(isp, org));
    }

    private static IpLookupResult ParseIpInfo(JsonElement root)
    {
        var ip = GetJsonString(root, "ip");
        var city = GetJsonString(root, "city");
        var region = GetJsonString(root, "region");
        var country = GetJsonString(root, "country");
        var org = GetJsonString(root, "org");

        return new IpLookupResult(ip, JoinLocation(country, region, city), org);
    }

    private static string GetJsonString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ToString();
    }

    private static string JoinLocation(params string[] parts) =>
        string.Join(" ", parts
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string ToChineseIpLocation(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "未知";

        foreach (var (from, to) in IpLocationPhraseTranslations)
            text = Regex.Replace(text, Regex.Escape(from), to, RegexOptions.IgnoreCase);

        var parts = Regex.Split(text, @"[\s,/|]+")
            .Select(TranslateIpLocationToken)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count == 0 ? value.Trim() : string.Join(" ", parts);
    }

    private static string TranslateIpLocationToken(string value)
    {
        var token = value.Trim();
        return token.ToLowerInvariant() switch
        {
            "china" or "cn" or "chn" => "中国",
            "jiangsu" => "江苏",
            "suzhou" => "苏州",
            "nanjing" => "南京",
            "wuxi" => "无锡",
            "changzhou" => "常州",
            "nantong" => "南通",
            "beijing" => "北京",
            "shanghai" => "上海",
            "tianjin" => "天津",
            "chongqing" => "重庆",
            "zhejiang" => "浙江",
            "hangzhou" => "杭州",
            "ningbo" => "宁波",
            "guangdong" => "广东",
            "guangzhou" => "广州",
            "shenzhen" => "深圳",
            "dongguan" => "东莞",
            "fujian" => "福建",
            "fuzhou" => "福州",
            "xiamen" => "厦门",
            "shandong" => "山东",
            "jinan" => "济南",
            "qingdao" => "青岛",
            "henan" => "河南",
            "zhengzhou" => "郑州",
            "hebei" => "河北",
            "shijiazhuang" => "石家庄",
            "hubei" => "湖北",
            "wuhan" => "武汉",
            "hunan" => "湖南",
            "changsha" => "长沙",
            "sichuan" => "四川",
            "chengdu" => "成都",
            "anhui" => "安徽",
            "hefei" => "合肥",
            "jiangxi" => "江西",
            "nanchang" => "南昌",
            "liaoning" => "辽宁",
            "shenyang" => "沈阳",
            "dalian" => "大连",
            "jilin" => "吉林",
            "heilongjiang" => "黑龙江",
            "harbin" => "哈尔滨",
            "shanxi" => "山西",
            "taiyuan" => "太原",
            "shaanxi" => "陕西",
            "xian" or "xi'an" => "西安",
            "guangxi" => "广西",
            "nanning" => "南宁",
            "yunnan" => "云南",
            "kunming" => "昆明",
            "guizhou" => "贵州",
            "guiyang" => "贵阳",
            "hainan" => "海南",
            "haikou" => "海口",
            "gansu" => "甘肃",
            "lanzhou" => "兰州",
            "qinghai" => "青海",
            "ningxia" => "宁夏",
            "xinjiang" => "新疆",
            "tibet" or "xizang" => "西藏",
            "mongolia" => "内蒙古",
            "taiwan" => "中国台湾",
            "hongkong" => "中国香港",
            "macau" or "macao" => "中国澳门",
            "us" or "usa" or "america" => "美国",
            "canada" => "加拿大",
            "japan" => "日本",
            "korea" => "韩国",
            "singapore" => "新加坡",
            "thailand" => "泰国",
            "vietnam" => "越南",
            "malaysia" => "马来西亚",
            "indonesia" => "印度尼西亚",
            "philippines" => "菲律宾",
            "australia" => "澳大利亚",
            "germany" => "德国",
            "france" => "法国",
            "italy" => "意大利",
            "spain" => "西班牙",
            "netherlands" => "荷兰",
            "russia" => "俄罗斯",
            "india" => "印度",
            "brazil" => "巴西",
            "syracuse" => "锡拉丘兹",
            "province" or "city" or "prefecture" or "municipality" => "",
            _ => token,
        };
    }

    private static string ToChineseIpOrg(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "未知";

        var normalized = Regex.Replace(text, @"[\s._-]+", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        if (normalized.Contains("chinatelecom") || normalized.Contains("telecom"))
            return "中国电信";
        if (normalized.Contains("chinaunicom") || normalized.Contains("unicom"))
            return "中国联通";
        if (normalized.Contains("chinamobile") || normalized.Contains("cmcc") || normalized.Contains("cmi"))
            return "中国移动";
        if (normalized.Contains("cernet"))
            return "中国教育网";
        if (normalized.Contains("alibaba") || normalized.Contains("aliyun"))
            return "阿里云";
        if (normalized.Contains("tencent"))
            return "腾讯云";
        if (normalized.Contains("huawei"))
            return "华为云";
        if (normalized.Contains("baidu"))
            return "百度云";

        foreach (var (from, to) in IpOrgPhraseTranslations)
            text = Regex.Replace(text, Regex.Escape(from), to, RegexOptions.IgnoreCase);
        return text;
    }

    private static readonly (string From, string To)[] IpLocationPhraseTranslations =
    [
        ("United States of America", "美国"),
        ("Hong Kong", "hongkong"),
        ("Macau", "macau"),
        ("Macao", "macau"),
        ("Inner Mongolia", "内蒙古"),
        ("United States", "美国"),
        ("United Kingdom", "英国"),
        ("South Korea", "韩国"),
        ("New York", "纽约州"),
        ("New Jersey", "新泽西州"),
        ("California", "加利福尼亚州"),
        ("Texas", "得克萨斯州"),
        ("Florida", "佛罗里达州"),
        ("Washington", "华盛顿州"),
        ("Illinois", "伊利诺伊州"),
        ("Virginia", "弗吉尼亚州"),
        ("Ohio", "俄亥俄州"),
        ("Georgia", "佐治亚州"),
        ("Pennsylvania", "宾夕法尼亚州"),
        ("North Carolina", "北卡罗来纳州"),
        ("South Carolina", "南卡罗来纳州"),
        ("Massachusetts", "马萨诸塞州"),
        ("Arizona", "亚利桑那州"),
        ("Nevada", "内华达州"),
        ("Oregon", "俄勒冈州"),
        ("Colorado", "科罗拉多州"),
        ("Michigan", "密歇根州"),
        ("Syracuse", "锡拉丘兹"),
        ("Los Angeles", "洛杉矶"),
        ("San Francisco", "旧金山"),
        ("San Jose", "圣何塞"),
        ("Seattle", "西雅图"),
        ("Chicago", "芝加哥"),
        ("Dallas", "达拉斯"),
        ("Houston", "休斯敦"),
        ("Miami", "迈阿密"),
        ("Atlanta", "亚特兰大"),
        ("Ashburn", "阿什本"),
    ];

    private static readonly (string From, string To)[] IpOrgPhraseTranslations =
    [
        ("China", "中国"),
        ("Telecom", "电信"),
        ("Unicom", "联通"),
        ("Mobile", "移动"),
    ];

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private sealed record IpLookupResult(string Ip, string Location, string Org);

    private void ClearFields()
    {
        NicknameBox.Text = "";
        LoginEmailBox.Text = "";
        PasswordBox.Text = "";
        StorageStateBox.Text = "";
        SeriesUrlBox.Text = TikTokUrls.DefaultSeriesDraftUrl;
        WorkspaceBox.Text = "";
        ArchiveRootBox.Text = "";
        DownloadWorkspaceBox.Text = "";
        ExcelReportBox.Text = "";
        DeleteVideosOnArchiveBox.IsChecked = true;
        CdpEndpointBox.Text = "";
        ContractIdBox.Text = "";
        ProofDeclarantCompanyNameBox.Text = "";
        ProofSealPathBox.Text = "";
        ProofCopyrightCompanyNameBox.Text = "";
        EpisodeScriptEpisodeCountBox.Value = TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount;
        AiScriptOutlineEpisodeCountBox.Value = TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount;
        RoleVectorCharacterCountBox.Value = TikTokAccountProfile.DefaultRoleVectorCharacterCount;
        RoleVectorMinimumCharacterCountBox.Value = TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount;
        CopyrightMaterials.Clear();
        ZeroCostAdsBox.IsChecked = false;
        DayZeroRoiBox.Value = (decimal)TikTokPublishOptions.DefaultDayZeroRoi;
        SubmitEnabledBox.IsChecked = true;
        MaxContinuousSilenceSecondsBox.Value = 20;
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

    private async void OnBrowseArchiveRootClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAsync(ArchiveRootBox, "选择当前账号归档目录");

    private async void OnBrowseDownloadWorkspaceClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAsync(DownloadWorkspaceBox, "选择下载工作目录");

    private async void OnBrowseExcelReportClick(object? sender, RoutedEventArgs e) =>
        await PickFileAsync(ExcelReportBox, "选择 Excel 报表文件", ["xlsx", "xls"]);

    private async void OnBrowseProofSealClick(object? sender, RoutedEventArgs e) =>
        await PickFileAsync(ProofSealPathBox, "选择公司印章图片", ["png", "jpg", "jpeg", "bmp"]);

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

    private static string NormalizeSubmitAction(string? value, bool? legacyEnabled = null)
    {
        var action = (value ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "none" => "none",
            "submit" => "submit",
            "save" => "save",
            _ => legacyEnabled.HasValue && !legacyEnabled.Value ? "none" : "submit",
        };
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
