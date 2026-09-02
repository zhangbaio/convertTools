using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouPersonalConfigDialog : Window
{
    private readonly KuaishouPersonalConfig _config;
    public IReadOnlyList<string> GenderOptions { get; } = ["男", "女"];
    public IReadOnlyList<string> FirstPageActions { get; } = ["draft", "next"];
    public IReadOnlyList<string> FinalActions { get; } = ["keep", "submit_review"];
    public IReadOnlyList<string> RunModes { get; } = ["auto", "create", "edit"];

    public KuaishouPersonalConfigDialog() : this(new KuaishouPersonalConfig()) { }

    private KuaishouPersonalConfigDialog(KuaishouPersonalConfig config)
    {
        _config = config;
        DataContext = this;
        InitializeComponent();
        LoadValues();
    }

    public static Task<KuaishouPersonalConfig?> ShowAsync(Window owner, KuaishouPersonalConfig config) =>
        new KuaishouPersonalConfigDialog(config).ShowDialog<KuaishouPersonalConfig?>(owner);

    private void LoadValues()
    {
        EntryUrlBox.Text = _config.EntryUrl;
        RealNameBox.Text = _config.RealName;
        GenderBox.SelectedItem = _config.Gender;
        NicknameBox.Text = _config.KuaishouNickname;
        KuaishouIdBox.Text = _config.KuaishouId;
        HeadlessBox.IsChecked = _config.Headless;
        KeepBrowserBox.IsChecked = _config.KeepBrowserOpenOnFailure;
        CommitmentPdfBox.Text = _config.CommitmentPdfPath;
        CategoryBox.Text = _config.Category;
        ContentTypeBox.Text = _config.ContentType;
        ProductionMethodBox.Text = _config.ProductionMethod;
        ProductionFormBox.Text = _config.ProductionForm;
        ProductionYearBox.Text = _config.ProductionYear;
        ProductionCostBox.Text = _config.ProductionCost;
        AverageDurationBox.Text = _config.AverageEpisodeMinutes;
        BroadcastPlatformBox.Text = _config.BroadcastPlatform;
        BroadcastChannelBox.Text = _config.BroadcastChannel;
        BroadcastDateBox.Text = _config.BroadcastDate;
        FinishedBox.IsChecked = _config.Finished;
        HasRecordNumberBox.IsChecked = _config.HasRecordNumber;
        ActorsBox.Text = _config.Actors;
        SaleTypeBox.Text = _config.SaleType;
        EpisodePriceBox.Text = _config.EpisodePrice;
        FreeEpisodeCountBox.Text = _config.FreeEpisodeCount.ToString();
        UnlockEpisodeCountBox.Text = _config.UnlockEpisodeCount.ToString();
        FirstPageActionBox.SelectedItem = _config.FirstPageAction;
        FinalActionBox.SelectedItem = _config.FinalAction;
        RunModeBox.SelectedItem = _config.RunMode;
        UploadTimeoutBox.Text = _config.UploadTimeoutMinutes.ToString();
        ForceRerunBox.IsChecked = _config.ForceRerun;
    }

    private async void PickCommitmentPdf_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择快手分账个人版承诺函 PDF",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF 文件") { Patterns = ["*.pdf"] }],
        });
        if (files.Count > 0) CommitmentPdfBox.Text = files[0].Path.LocalPath;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FreeEpisodeCountBox.Text, out var free) || free < 0 ||
            !int.TryParse(UnlockEpisodeCountBox.Text, out var unlock) || unlock < 0 ||
            !int.TryParse(UploadTimeoutBox.Text, out var timeout) || timeout is < 5 or > 240)
        {
            ValidationText.Text = "免费集数、解锁集数必须为非负整数；上传超时必须为 5–240 分钟。";
            return;
        }
        if (!string.IsNullOrWhiteSpace(CommitmentPdfBox.Text) &&
            (!File.Exists(CommitmentPdfBox.Text) || !string.Equals(Path.GetExtension(CommitmentPdfBox.Text), ".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            ValidationText.Text = "承诺函必须是存在的 PDF 文件。";
            return;
        }

        _config.EntryUrl = EntryUrlBox.Text?.Trim() ?? string.Empty;
        _config.RealName = RealNameBox.Text?.Trim() ?? string.Empty;
        _config.Gender = GenderBox.SelectedItem?.ToString() ?? "男";
        _config.KuaishouNickname = NicknameBox.Text?.Trim() ?? string.Empty;
        _config.KuaishouId = KuaishouIdBox.Text?.Trim() ?? string.Empty;
        _config.Headless = HeadlessBox.IsChecked == true;
        _config.KeepBrowserOpenOnFailure = KeepBrowserBox.IsChecked == true;
        _config.CommitmentPdfPath = CommitmentPdfBox.Text?.Trim() ?? string.Empty;
        _config.Category = CategoryBox.Text?.Trim() ?? string.Empty;
        _config.ContentType = ContentTypeBox.Text?.Trim() ?? string.Empty;
        _config.ProductionMethod = ProductionMethodBox.Text?.Trim() ?? string.Empty;
        _config.ProductionForm = ProductionFormBox.Text?.Trim() ?? string.Empty;
        _config.ProductionYear = ProductionYearBox.Text?.Trim() ?? string.Empty;
        _config.ProductionCost = ProductionCostBox.Text?.Trim() ?? string.Empty;
        _config.AverageEpisodeMinutes = AverageDurationBox.Text?.Trim() ?? string.Empty;
        _config.BroadcastPlatform = BroadcastPlatformBox.Text?.Trim() ?? string.Empty;
        _config.BroadcastChannel = BroadcastChannelBox.Text?.Trim() ?? string.Empty;
        _config.BroadcastDate = BroadcastDateBox.Text?.Trim() ?? string.Empty;
        _config.Finished = FinishedBox.IsChecked == true;
        _config.HasRecordNumber = HasRecordNumberBox.IsChecked == true;
        _config.Actors = ActorsBox.Text?.Trim() ?? string.Empty;
        _config.SaleType = SaleTypeBox.Text?.Trim() ?? string.Empty;
        _config.EpisodePrice = EpisodePriceBox.Text?.Trim() ?? string.Empty;
        _config.FreeEpisodeCount = free;
        _config.UnlockEpisodeCount = unlock;
        _config.FirstPageAction = FirstPageActionBox.SelectedItem?.ToString() ?? "draft";
        _config.FinalAction = FinalActionBox.SelectedItem?.ToString() ?? "keep";
        _config.RunMode = RunModeBox.SelectedItem?.ToString() ?? "auto";
        _config.UploadTimeoutMinutes = timeout;
        _config.ForceRerun = ForceRerunBox.IsChecked == true;
        Close(_config);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
