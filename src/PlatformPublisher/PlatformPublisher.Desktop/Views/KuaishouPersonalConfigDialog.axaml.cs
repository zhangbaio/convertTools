using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using ChannelsAccount = ChannelsPublisher.Core.Models.PublishAccount;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;
using AccountStore = ChannelsPublisher.Core.Services.AccountStore;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouPersonalConfigDialog : UserControl
{
    private KuaishouPersonalConfig _config = new();
    private PublishPlatform _platform = PublishPlatform.KuaishouPersonalRevenue;
    private ChannelsAccount? _account;
    private readonly Control[] _editableFields;
    private string[] _cleanValues = [];
    private bool _legacyImportPending;
    private bool _usesWorkspaceNavigation;
    private bool _saving;
    public event Func<string, PublishPlatform, string, Task>? ConfigSaved;
    public event Func<ChannelsAccount, string, Task<bool>>? ConfirmAccountEdition;
    public event Func<ChannelsAccount, string, Task>? AccountEditionApplied;
    public event Action<IReadOnlyList<ChannelsAccount>>? AccountsImported;
    private AccountStore? _accountStore;

    public KuaishouPersonalConfigDialog()
    {
        DataContext = this;
        InitializeComponent();
        _editableFields = ConfigurationTabs.Items.OfType<TabItem>()
            .SelectMany(tab => tab.GetLogicalDescendants())
            .OfType<Control>()
            .Where(control => control.Name is not null &&
                (control is TextBox { IsReadOnly: false } or ComboBox or ToggleButton))
            .ToArray();
        LoadValues();
        AiEditor.LoadAccount(null);
        CaptureClean();
        UpdateHeading();
    }

    public void UseAccountStore(AccountStore accountStore) => _accountStore = accountStore;

    internal void UseAccountAiStore(KuaishouAccountAiStore store) => AiEditor.Store = store;

    internal string LegacyVideoDedupTemplateId => _config.VideoDedupTemplateId;

    internal void UseWorkspaceNavigation(Control workDirectoryEditor)
    {
        _usesWorkspaceNavigation = true;
        foreach (var tab in ConfigurationTabs.Items.OfType<TabItem>())
            tab.IsVisible = false;
        WorkDirectoryHost.Content = workDirectoryEditor;
        WorkDirectoryHost.IsVisible = true;
    }

    public void SelectWorkspacePage(bool shelf) => ConfigurationTabs.SelectedIndex = shelf ? 1 : 0;

    public async Task<bool> TryLeaveAsync(Window owner)
    {
        if (_saving) return false;
        if (StandaloneDedupHost.Content is KuaishouVideoDedupView dedup && !await dedup.TryLeaveAsync(owner))
            return false;
        if (!HasUnsavedChanges()) return true;
        var save = await ConfirmDialog.ShowSaveChangesAsync(owner, "保存快手分账配置",
            $"「{_account?.Name ?? "当前账号"}」的快手分账配置已修改，是否保存？");
        if (save is null) return false;
        if (save == false)
        {
            LoadAccount(_account, _platform);
            return true;
        }
        return await SaveCurrentAsync();
    }

    public void LoadAccount(ChannelsAccount? account, PublishPlatform platform)
    {
        _account = account;
        _platform = platform is PublishPlatform.KuaishouEnterpriseRevenue
            ? PublishPlatform.KuaishouEnterpriseRevenue
            : PublishPlatform.KuaishouPersonalRevenue;
        _config = LoadStoredConfig();
        if (!_usesWorkspaceNavigation)
        {
            var dedup = StandaloneDedupHost.Content as KuaishouVideoDedupView ?? new KuaishouVideoDedupView();
            dedup.LoadAccount(account?.Id, account?.Name, LegacyVideoDedupTemplateId);
            StandaloneDedupHost.Content = dedup;
        }
        LoadValues();
        AiEditor.LoadAccount(account?.Id);
        CaptureClean();
        UpdateHeading();
    }

    private void LoadValues()
    {
        var edition = _account?.KuaishouAccountEdition ?? "";
        EditionPersonalBox.IsChecked = string.Equals(edition, "personal", StringComparison.OrdinalIgnoreCase);
        EditionEnterpriseBox.IsChecked = string.Equals(edition, "enterprise", StringComparison.OrdinalIgnoreCase);
        RemoteDispatchBox.IsChecked = _account?.ParticipatesInXingeRemoteDispatch == true;
        ApiBaseUrlBox.Text = _config.ApiBaseUrl;
        AdvertiserIdBox.Text = FirstNonEmpty(_config.AdvertiserId, _config.KuaishouId);
        NicknameBox.Text = _config.KuaishouNickname;
        AccessTokenBox.Text = _config.AccessToken;
        TokenHeaderBox.Text = _config.TokenHeader;

        CommitmentRecipientBox.Text = _config.CommitmentRecipientCompanyName;

        FillChoice(ContentTypeBox, KuaishouFormChoices.ContentTypes, _config.ContentType, "漫剧");
        FillChoice(ComicMethodBox, KuaishouFormChoices.ComicMethods, _config.ProductionMethod, "3");
        FillChoice(AuthorDeclarationBox, KuaishouFormChoices.AuthorDeclarations, _config.AuthorDeclaration, "1");
        FillChoice(ProductionFormBox, KuaishouFormChoices.ProductionForms, _config.ProductionForm, "auto");
        ProductionYearBox.Text = _config.ProductionYear;
        AverageDurationBox.Text = _config.AverageEpisodeMinutes;
        ProductionCostBox.Text = _config.ProductionCost;
        FillChoice(SaleTypeBox, KuaishouFormChoices.SaleTypes, NormalizeSaleType(_config.SaleType), "3");
        FreeEpisodeCountBox.Text = _config.FreeEpisodeCount.ToString();
        UnlockEpisodeCountBox.Text = _config.UnlockEpisodeCount.ToString();
        EpisodePriceBox.Text = _config.EpisodePrice;
        SeriesPriceBox.Text = _config.SeriesPrice;
        FillChoice(CopyrightProofTypeBox, KuaishouFormChoices.CopyrightProofTypes, _config.CopyrightProofType, "1");
        UpdateSaleTypeRows();

        BroadcastPlatformBox.Text = _config.BroadcastPlatform;
        FillChoice(BroadcastChannelBox, KuaishouFormChoices.BroadcastChannels, _config.BroadcastChannel, "小屏APP");
        var broadcastMode = string.IsNullOrWhiteSpace(_config.BroadcastTimeMode) ? "today" : _config.BroadcastTimeMode.Trim();
        var fixedDate = !string.Equals(broadcastMode, "today", StringComparison.OrdinalIgnoreCase);
        FillChoice(BroadcastTimeModeBox, KuaishouFormChoices.BroadcastTimeModes, fixedDate ? "fixed" : "today", "today");
        BroadcastDateBox.Text = fixedDate ? broadcastMode : _config.BroadcastDate;
        BroadcastDateField.IsVisible = fixedDate;
        ActorLibraryBox.Text = _config.ActorLibraryText;

        QueueParallelBox.Text = _config.QueueMaxParallelProjects.ToString();
        SeriesBaseInfoPathBox.Text = _config.SeriesBaseInfoPath;
        OnlineOfflinePathBox.Text = _config.OnlineOfflinePath;
        FillChoice(SubmitPreCheckBox, KuaishouFormChoices.SubmitPreWaits, _config.SubmitPreCheckWaitSeconds.ToString(), "0");

        FillTemplate(PersonalTemplateBox, FirstNonEmpty(
            _config.PersonalProjectImageTemplateId,
            _config.EnterpriseProjectImageTemplateId,
            _config.ProjectImageTemplateId));

        ScheduledShelfBox.IsChecked = _config.ScheduledShelfEnabled;
        FillChoice(ScheduledShelfModeBox, KuaishouFormChoices.ScheduledShelfModes, _config.ScheduledShelfMode, "interval");
        ScheduledShelfIntervalBox.Text = _config.ScheduledShelfIntervalHours.ToString();
        ScheduledShelfDailyBox.Text = KuaishouScheduledShelfSchedule.NormalizeClock(_config.ScheduledShelfDailyTime);
        ScheduledShelfOnlyWhenIdleBox.IsChecked = _config.ScheduledShelfOnlyWhenIdle;
        ScheduledShelfMaxBox.Text = _config.ScheduledShelfMaxItemsPerRound.ToString();
        UpdateScheduledShelfModeRows();

        AutoOnlineBox.IsChecked = _config.AutoOnlineEnabled;
        AutoOnlineIntervalBox.Text = _config.AutoOnlineIntervalMinutes.ToString();
        AutoOnlineMaxItemsBox.Text = _config.AutoOnlineMaxItemsPerRound.ToString();

        UsernameBox.Text = FirstNonEmpty(_config.Username, _config.RealName);
        FillChoice(GenderBox, KuaishouFormChoices.Genders, _config.Gender, "");
        CommitmentPdfBox.Text = _config.CommitmentPdfPath;
        var uploadTransports = KuaishouFormChoices.UploadTransports;
        var transport = KuaishouFormChoices.CanonicalValue(uploadTransports, _config.UploadTransport, KuaishouUploadTransport.Api);
        FillChoice(UploadTransportBox, uploadTransports,
            KuaishouUploadTransport.IsApi(transport) ? KuaishouUploadTransport.Api : KuaishouUploadTransport.Playwright,
            KuaishouUploadTransport.Api);
        FillChoice(RevenueShareBrowserModeBox, KuaishouFormChoices.BrowserModes, _config.RevenueShareHeadless ? "true" : "false", "false");
        KeepBrowserBox.IsChecked = _config.KeepBrowserOpenOnFailure;
        UpdateSubmissionOptions();

        LoadContentPolicy();
    }

    private void LoadContentPolicy()
    {
        TitlePolicySummary.Text = KuaishouContentPolicyText.BaselineSummary(
            KuaishouContentPolicyDefaults.TitlePolicyJson, "categories");
        SynopsisTermsSummary.Text = KuaishouContentPolicyText.BaselineSummary(
            KuaishouContentPolicyDefaults.SynopsisPolicyJson, "categories");
        ConceptGroupsSummary.Text = KuaishouContentPolicyText.BaselineSummary(
            KuaishouContentPolicyDefaults.SynopsisPolicyJson, "concept_groups");
        TitlePolicyBox.Text = KuaishouContentPolicyText.FormatCategories(
            _config.DramaTitlePolicyJson, KuaishouContentPolicyDefaults.TitlePolicyJson, "categories", "disabled");
        SynopsisTermsBox.Text = KuaishouContentPolicyText.FormatCategories(
            _config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson, "categories", "disabled");
        ConceptGroupsBox.Text = KuaishouContentPolicyText.FormatCategories(
            _config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson, "concept_groups", "disabled_concepts");
        CombinationRulesBox.Text = KuaishouContentPolicyText.FormatRules(
            _config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson);
    }

    private void RestoreContentPolicy_Click(object? sender, RoutedEventArgs e)
    {
        _config.DramaTitlePolicyJson = KuaishouContentPolicyDefaults.TitlePolicyJson;
        _config.SynopsisPolicyJson = KuaishouContentPolicyDefaults.SynopsisPolicyJson;
        LoadContentPolicy();
    }

    private async void PickCommitmentPdf_Click(object? sender, RoutedEventArgs e)
    {
        var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择承诺函 PDF",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF 文件") { Patterns = ["*.pdf"] }],
        });
        if (files.Count > 0) CommitmentPdfBox.Text = files[0].Path.LocalPath;
    }

    private async void ImportLegacy_Click(object? sender, RoutedEventArgs e)
    {
        var path = KuaishouLegacyConfigImporter.DefaultSettingsPath;
        if (!File.Exists(path))
        {
            var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择旧版短剧助手 settings.json",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0) return;
            path = files[0].Path.LocalPath;
        }

        try
        {
            var beforeImport = JsonSerializer.Serialize(_config);
            var result = KuaishouLegacyConfigImporter.Import(path, _config, _platform);
            _legacyImportPending |= beforeImport != JsonSerializer.Serialize(_config);
            LoadValues();
            ValidationText.Foreground = Avalonia.Media.Brushes.ForestGreen;
            ValidationText.Text = $"已从旧版配置导入 {result.ImportedFields} 个字段，其中 {result.ImportedSensitiveFields} 个凭据字段将在保存时加密。";
        }
        catch (Exception ex)
        {
            ValidationText.Foreground = Avalonia.Media.Brushes.Red;
            ValidationText.Text = "旧版配置导入失败：" + ex.Message;
        }
    }

    private async void ImportExportedSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (_accountStore is null)
        {
            ValidationText.Text = "账号库尚未就绪，不能导入配置。";
            return;
        }
        if (HasUnsavedChanges())
        {
            ValidationText.Text = "请先保存或放弃当前修改，再导入微信工具配置。";
            return;
        }

        var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择微信工具导出的快手分账配置",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("微信工具配置") { Patterns = ["*.json", "*.zip"] }],
        });
        if (files.Count == 0) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        try
        {
            using var package = KuaishouSettingsExportPackage.Open(files[0].Path.LocalPath);
            var importer = new KuaishouExportedSettingsImporter(_accountStore);
            var preview = importer.Preview(package);
            if (preview.Accounts.Count == 0)
            {
                ValidationText.Foreground = Avalonia.Media.Brushes.Red;
                ValidationText.Text = importer.Describe(preview);
                return;
            }
            var confirmed = await ConfirmDialog.ShowAsync(owner, "导入微信工具配置", importer.Describe(preview), 680, 460);
            if (!confirmed) return;
            var result = await importer.ApplyAsync(package);
            AccountsImported?.Invoke(result.Accounts);
            LoadAccount(_account, _platform);
            ValidationText.Foreground = Avalonia.Media.Brushes.ForestGreen;
            ValidationText.Text = $"已导入快手分账配置：新建 {result.Created} 个，更新 {result.Updated} 个，登录态 {result.AuthFiles} 份，附件 {result.Assets} 个。上传记录新导入 {result.ImportedUploads} 部，合并 {result.MergedUploads} 部，跳过重复 {result.SkippedUploads} 部，无法归类 {result.UnmatchedUploads} 部。";
            if (result.MissingPaths.Count > 0)
                ValidationText.Text += $" 有 {result.MissingPaths.Count} 个路径在这台电脑上不存在。";
        }
        catch (Exception ex)
        {
            ValidationText.Foreground = Avalonia.Media.Brushes.Red;
            ValidationText.Text = "导入微信工具配置失败：" + ex.Message;
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveCurrentAsync();

    private bool ApplyToConfig()
    {
        if (!int.TryParse(FreeEpisodeCountBox.Text, out var free) || free < 0 ||
            !int.TryParse(UnlockEpisodeCountBox.Text, out var unlock) || unlock < 0)
        {
            ValidationText.Text = "免费集数和解锁集数必须为非负整数。";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(CommitmentPdfBox.Text) &&
            (!File.Exists(CommitmentPdfBox.Text) || !string.Equals(Path.GetExtension(CommitmentPdfBox.Text), ".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            ValidationText.Text = "承诺函必须是存在的 PDF 文件。";
            return false;
        }

        _config.ApiBaseUrl = ApiBaseUrlBox.Text?.Trim() ?? string.Empty;
        _config.AdvertiserId = AdvertiserIdBox.Text?.Trim() ?? string.Empty;
        _config.KuaishouId = _config.AdvertiserId;
        _config.KuaishouNickname = NicknameBox.Text?.Trim() ?? string.Empty;
        _config.AccessToken = AccessTokenBox.Text ?? string.Empty;
        _config.TokenHeader = TokenHeaderBox.Text?.Trim() ?? "Access-Token";

        _config.CommitmentRecipientCompanyName = CommitmentRecipientBox.Text?.Trim() ?? string.Empty;

        _config.ContentType = ChoiceValue(ContentTypeBox, "漫剧");
        _config.ProductionMethod = ChoiceValue(ComicMethodBox, "3");
        _config.AuthorDeclaration = ChoiceValue(AuthorDeclarationBox, "1");
        _config.ProductionForm = ChoiceValue(ProductionFormBox, "auto");
        _config.ProductionYear = ProductionYearBox.Text?.Trim() ?? string.Empty;
        _config.AverageEpisodeMinutes = AverageDurationBox.Text?.Trim() ?? string.Empty;
        _config.ProductionCost = ProductionCostBox.Text?.Trim() ?? string.Empty;
        _config.SaleType = ChoiceValue(SaleTypeBox, "3");
        _config.FreeEpisodeCount = free;
        _config.UnlockEpisodeCount = unlock;
        _config.EpisodePrice = EpisodePriceBox.Text?.Trim() ?? string.Empty;
        _config.SeriesPrice = SeriesPriceBox.Text?.Trim() ?? string.Empty;
        _config.CopyrightProofType = ChoiceValue(CopyrightProofTypeBox, "1");

        _config.BroadcastPlatform = BroadcastPlatformBox.Text?.Trim() ?? string.Empty;
        _config.BroadcastChannel = ChoiceValue(BroadcastChannelBox, "小屏APP");
        var timeMode = ChoiceValue(BroadcastTimeModeBox, "today");
        _config.BroadcastTimeMode = timeMode == "fixed"
            ? (BroadcastDateBox.Text?.Trim() ?? string.Empty)
            : "today";
        if (timeMode == "fixed" && !string.IsNullOrWhiteSpace(BroadcastDateBox.Text))
            _config.BroadcastDate = BroadcastDateBox.Text.Trim();
        _config.ActorLibraryText = ActorLibraryBox.Text ?? string.Empty;

        _config.QueueMaxParallelProjects = Math.Clamp(Positive(QueueParallelBox.Text, 1), 1, 5);
        _config.SeriesBaseInfoPath = SeriesBaseInfoPathBox.Text?.Trim() ?? string.Empty;
        _config.OnlineOfflinePath = OnlineOfflinePathBox.Text?.Trim() ?? string.Empty;
        _config.SubmitPreCheckWaitSeconds = int.TryParse(ChoiceValue(SubmitPreCheckBox, "0"), out var preWait) ? preWait : 0;

        var settlementTemplate = TemplateValue(PersonalTemplateBox);
        _config.PersonalProjectImageTemplateId = settlementTemplate;
        _config.EnterpriseProjectImageTemplateId = settlementTemplate;
        _config.ProjectImageTemplateId = settlementTemplate;

        _config.ScheduledShelfEnabled = ScheduledShelfBox.IsChecked == true;
        _config.ScheduledShelfMode = ChoiceValue(ScheduledShelfModeBox, "interval");
        _config.ScheduledShelfIntervalHours = Math.Clamp(Positive(ScheduledShelfIntervalBox.Text, 1), 1, 24);
        _config.ScheduledShelfDailyTime = KuaishouScheduledShelfSchedule.NormalizeClock(ScheduledShelfDailyBox.Text);
        _config.ScheduledShelfOnlyWhenIdle = ScheduledShelfOnlyWhenIdleBox.IsChecked == true;
        _config.ScheduledShelfMaxItemsPerRound = Math.Clamp(Positive(ScheduledShelfMaxBox.Text, 50), 1, 200);
        _config.AutoOnlineEnabled = AutoOnlineBox.IsChecked == true;
        _config.AutoOnlineIntervalMinutes = Positive(AutoOnlineIntervalBox.Text, 30);
        _config.AutoOnlineMaxItemsPerRound = Positive(AutoOnlineMaxItemsBox.Text, 30);

        _config.Username = UsernameBox.Text?.Trim() ?? string.Empty;
        _config.RealName = _config.Username;
        _config.Gender = ChoiceValue(GenderBox, "");
        _config.CommitmentPdfPath = CommitmentPdfBox.Text?.Trim() ?? string.Empty;
        _config.UploadTransport = ChoiceValue(UploadTransportBox, KuaishouUploadTransport.Api);
        _config.RevenueShareHeadless = ChoiceValue(RevenueShareBrowserModeBox, "false") == "true";
        _config.Headless = _config.RevenueShareHeadless;
        _config.KeepBrowserOpenOnFailure = KeepBrowserBox.IsChecked == true;

        _config.DramaTitlePolicyJson = KuaishouContentPolicyText.BuildTitlePolicy(
            TitlePolicyBox.Text ?? string.Empty, KuaishouContentPolicyDefaults.TitlePolicyJson);
        _config.SynopsisPolicyJson = KuaishouContentPolicyText.BuildSynopsisPolicy(
            SynopsisTermsBox.Text ?? string.Empty, ConceptGroupsBox.Text ?? string.Empty,
            CombinationRulesBox.Text ?? string.Empty, KuaishouContentPolicyDefaults.SynopsisPolicyJson);
        _config.ContentPolicyMigrated = true;
        return true;
    }

    private void SaleTypeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSaleTypeRows();

    private void PublishViewport_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (PublishForm is null || e.NewSize.Width <= 0)
            return;

        var width = Math.Min(e.NewSize.Width, PublishForm.MaxWidth);
        PublishForm.Width = width;
        var columns = width < 580 ? 1 : 2;
        foreach (var row in PublishForm.GetLogicalDescendants().OfType<UniformGrid>())
            row.Columns = columns;
    }

    private void UploadTransportBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateSubmissionOptions();

    private void UpdateSubmissionOptions()
    {
        if (PageUploadOptions is null || LegacyOnlineOptions is null || SubmitPreCheckOptions is null)
            return;

        var pageUpload = ChoiceValue(UploadTransportBox, KuaishouUploadTransport.Api) == KuaishouUploadTransport.Playwright;
        PageUploadOptions.IsVisible = pageUpload;
        LegacyOnlineOptions.IsVisible = pageUpload;
        SubmitPreCheckOptions.IsVisible = pageUpload;
    }

    private void UpdateSaleTypeRows()
    {
        if (SeriesPriceField is null)
            return;

        var code = KuaishouFormChoices.SaleTypeCode(ChoiceValue(SaleTypeBox, "3"));
        var adUnlock = code == 3;
        UnlockCountField.IsVisible = adUnlock;
        EpisodePriceField.IsVisible = code == 2;
        SeriesPriceField.IsVisible = code is 0 or 2;
    }

    private void BroadcastTimeModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BroadcastDateField is null || BroadcastTimeModeBox is null) return;
        BroadcastDateField.IsVisible = ChoiceValue(BroadcastTimeModeBox, "today") == "fixed";
    }

    private void ScheduledShelfModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateScheduledShelfModeRows();

    private void UpdateScheduledShelfModeRows()
    {
        if (ScheduledShelfModeBox is null) return;
        var daily = ChoiceValue(ScheduledShelfModeBox, "interval") == "daily";
        ScheduledShelfIntervalLabel.IsVisible = !daily;
        ScheduledShelfIntervalBox.IsVisible = !daily;
        ScheduledShelfDailyLabel.IsVisible = daily;
        ScheduledShelfDailyBox.IsVisible = daily;
    }

    private static void FillChoice(ComboBox box, IReadOnlyList<KuaishouFormChoice> choices, string? raw, string fallback)
    {
        var value = KuaishouFormChoices.CanonicalValue(choices, raw, fallback);
        var items = choices.ToList();
        if (items.All(choice => !string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)))
            items.Add(new KuaishouFormChoice(string.IsNullOrWhiteSpace(raw) ? value : raw!.Trim(), value));
        box.ItemsSource = items;
        box.SelectedItem = items.First(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase));
    }

    private static string ChoiceValue(ComboBox box, string fallback) =>
        box.SelectedItem is KuaishouFormChoice choice && !string.IsNullOrWhiteSpace(choice.Value)
            ? choice.Value
            : fallback;

    private static void FillTemplate(ComboBox box, string? templateId)
    {
        var options = TikTokProjectImageTemplateCatalog.BuiltInOptions
            .Select(option => new KuaishouFormChoice(option.SelectionLabel, option.Id))
            .ToList();
        var value = TikTokProjectImageTemplateCatalog.NormalizeId(templateId);
        box.ItemsSource = options;
        box.SelectedItem = options.First(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));
    }

    private static string TemplateValue(ComboBox box) =>
        box.SelectedItem is KuaishouFormChoice choice ? choice.Value : string.Empty;

    private static string NormalizeSaleType(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Contains("广告", StringComparison.Ordinal)) return "3";
        if (text.Contains("单集", StringComparison.Ordinal)) return "2";
        if (text.Contains("全剧", StringComparison.Ordinal)) return "0";
        return text;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static int Positive(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => LoadAccount(_account, _platform);

    private async Task<bool> SaveCurrentAsync()
    {
        if (_saving) return false;
        _saving = true;
        var enabled = IsEnabled;
        IsEnabled = false;
        try { return await SaveAccountAsync(); }
        finally
        {
            IsEnabled = enabled;
            _saving = false;
        }
    }

    private async Task<bool> SaveAccountAsync()
    {
        if (_account is null)
        {
            ValidationText.Text = "请先在左侧选择账号。";
            return false;
        }
        if (!ApplyToConfig()) return false;
        var accountId = _account.Id;
        var aiOptions = AiEditor.PendingOptions();
        var edition = SelectedEdition();
        var editionChanged = !string.Equals(edition, _account.KuaishouAccountEdition ?? "", StringComparison.OrdinalIgnoreCase);
        if (editionChanged)
        {
            if (string.IsNullOrWhiteSpace(edition))
            {
                ValidationText.Foreground = Avalonia.Media.Brushes.Red;
                ValidationText.Text = "请选择账号类型。";
                return false;
            }
            if (ConfirmAccountEdition is null || !await ConfirmAccountEdition(_account, edition))
            {
                ValidationText.Foreground = Avalonia.Media.Brushes.Red;
                ValidationText.Text = "账号类型未保存。";
                return false;
            }
        }
        var issues = KuaishouConfigurationValidator.Validate(_config);
        if (issues.Count > 0)
        {
            ValidationText.Foreground = Avalonia.Media.Brushes.Red;
            ValidationText.Text = string.Join("；", issues);
            return false;
        }
        var path = ResolveConfigPath(createDefault: true);
        await _config.SaveAsync(path);
        if (aiOptions is not null)
        {
            try { AiEditor.Save(accountId, aiOptions); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                System.Security.Cryptography.CryptographicException or Microsoft.Data.Sqlite.SqliteException)
            {
                ValidationText.Foreground = Avalonia.Media.Brushes.Red;
                ValidationText.Text = "发布配置已写入，AI 配置保存失败。请重试保存。";
                return false;
            }
        }
        _account.XingeRemoteDispatchConfigured = true;
        _account.XingeRemoteDispatchEnabled = RemoteDispatchBox.IsChecked == true;
        var stored = _accountStore?.Accounts.FirstOrDefault(item =>
            string.Equals(item.Id, _account.Id, StringComparison.OrdinalIgnoreCase));
        if (stored is not null && !ReferenceEquals(stored, _account))
        {
            stored.XingeRemoteDispatchConfigured = _account.XingeRemoteDispatchConfigured;
            stored.XingeRemoteDispatchEnabled = _account.XingeRemoteDispatchEnabled;
        }
        _accountStore?.Update(_account);
        CaptureClean();
        ValidationText.Foreground = Avalonia.Media.Brushes.ForestGreen;
        ValidationText.Text = "已保存。";
        if (ConfigSaved is not null) await ConfigSaved(_account.Id, _platform, path);
        if (editionChanged && AccountEditionApplied is not null)
            await AccountEditionApplied(_account, edition);
        return true;
    }

    private KuaishouPersonalConfig LoadStoredConfig()
    {
        if (_account is null) return new KuaishouPersonalConfig();
        var path = ResolveConfigPath(createDefault: false);
        return KuaishouPersonalConfig.Load(new PublishJob
        {
            Platform = _platform,
            AccountId = _account.Id,
            ConfigPath = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : string.Empty,
        });
    }

    private string ResolveConfigPath(bool createDefault)
    {
        var personal = _account?.KuaishouPersonalConfigPath?.Trim();
        var enterprise = _account?.KuaishouEnterpriseConfigPath?.Trim();
        if (!string.IsNullOrWhiteSpace(personal) && File.Exists(personal)) return Path.GetFullPath(personal);
        if (!string.IsNullOrWhiteSpace(enterprise) && File.Exists(enterprise)) return Path.GetFullPath(enterprise);
        if (!string.IsNullOrWhiteSpace(personal)) return Path.GetFullPath(personal);
        if (!string.IsNullOrWhiteSpace(enterprise)) return Path.GetFullPath(enterprise);
        return createDefault && _account is not null
            ? KuaishouPersonalConfig.DefaultConfigPath(_account.Id, PublishPlatform.KuaishouPersonalRevenue)
            : string.Empty;
    }

    public string? AccountId => _account?.Id;
    public PublishPlatform CurrentPlatform => _platform;

    private string SelectedEdition() =>
        EditionEnterpriseBox.IsChecked == true ? "enterprise"
        : EditionPersonalBox.IsChecked == true ? "personal"
        : "";

    // Compare the editable values as loaded. Validation and legacy value normalization belong to saving.
    private bool HasUnsavedChanges() =>
        _legacyImportPending || AiEditor.HasChanges || !CaptureFormValues().SequenceEqual(_cleanValues);

    private void CaptureClean()
    {
        _cleanValues = CaptureFormValues().ToArray();
        AiEditor.CaptureClean();
        _legacyImportPending = false;
    }

    private IEnumerable<string> CaptureFormValues() => _editableFields.Select(control => control switch
    {
        TextBox text => text.Text ?? "",
        ComboBox { SelectedItem: KuaishouFormChoice choice } => choice.Value,
        ToggleButton toggle => toggle.IsChecked == true ? "true" : "false",
        _ => "",
    });

    private void UpdateHeading()
    {
        AccountHeading.Text = _account is null ? "快手分账配置" : _account.Name;
        LoginStatusText.Text = _account is null ? "请选择发布账号" : File.Exists(_config.AuthStatePath)
            ? "已保存快手授权文件（可能已登录）"
            : "尚未保存快手授权文件";
    }

    private IStorageProvider Files =>
        TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("当前页面无法选择文件。");
}
