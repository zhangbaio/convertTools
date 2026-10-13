using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Analytics.Services;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Analytics;
using PlatformPublisher.Weixin.Analytics;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class AnalyticsViewModel : ObservableObject
{
    private readonly AnalyticsRepository _repository;
    private readonly AnalyticsQueryService _queryService;
    private readonly LocalPublishActivitySyncService _activitySync;
    private readonly AnalyticsCollectionCoordinator _coordinator;
    private readonly WeixinAnalyticsCollector _weixinCollector;
    private readonly KuaishouAnalyticsCollector _kuaishouCollector;
    private readonly PublishJobStore _jobStore;
    private readonly YunfanAnalyticsImporter _legacyImporter;
    private Func<IReadOnlyList<AnalyticsAccount>> _accountProvider = () => [];
    private Func<string, CancellationToken, Task<string>> _weixinCdpProvider = (_, _) => throw new InvalidOperationException("视频号会话未绑定。");
    private CancellationTokenSource? _cts;

    public AnalyticsViewModel(AnalyticsRepository repository, AnalyticsQueryService queryService,
        LocalPublishActivitySyncService activitySync, AnalyticsCollectionCoordinator coordinator,
        WeixinAnalyticsCollector weixinCollector, KuaishouAnalyticsCollector kuaishouCollector,
        PublishJobStore jobStore, YunfanAnalyticsImporter legacyImporter)
    {
        _repository = repository; _queryService = queryService; _activitySync = activitySync;
        _coordinator = coordinator; _weixinCollector = weixinCollector; _kuaishouCollector = kuaishouCollector;
        _jobStore = jobStore;
        _legacyImporter = legacyImporter;
        var today = DateOnly.FromDateTime(DateTime.Today);
        _fromDate = new DateOnly(today.Year, today.Month, 1).ToString("yyyy-MM-dd");
        _toDate = today.AddDays(-1).ToString("yyyy-MM-dd");
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(true), () => !IsBusy);
        ReloadCommand = new AsyncRelayCommand(() => RefreshAsync(false), () => !IsBusy);
        BackfillCommand = new AsyncRelayCommand(BackfillAsync, () => !IsBusy);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ImportLegacyCommand = new AsyncRelayCommand(ImportLegacyAsync, () => !IsBusy);
    }

    public void Bind(Func<IReadOnlyList<AnalyticsAccount>> accountProvider,
        Func<string, CancellationToken, Task<string>> weixinCdpProvider)
    {
        _accountProvider = accountProvider;
        _weixinCdpProvider = weixinCdpProvider;
    }

    public ObservableCollection<AnalyticsAccountRowViewModel> AccountRows { get; } = [];
    public ObservableCollection<AnalyticsPlatformRowViewModel> PlatformRows { get; } = [];
    public ObservableCollection<AnalyticsDailyRowViewModel> DailyRows { get; } = [];
    public ObservableCollection<AnalyticsIncomeRowViewModel> IncomeRows { get; } = [];
    public string[] PlatformChoices { get; } = ["全部平台", "视频号", "快手个人版"];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }
    public IAsyncRelayCommand BackfillCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IAsyncRelayCommand ImportLegacyCommand { get; }

    [ObservableProperty] private string _fromDate;
    [ObservableProperty] private string _toDate;
    [ObservableProperty] private string _selectedPlatform = "全部平台";
    [ObservableProperty] private string _keyword = string.Empty;
    [ObservableProperty] private string _statusMessage = "尚未加载统计数据。";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _accountCount;
    [ObservableProperty] private int _taskCount;
    [ObservableProperty] private int _succeededCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private long _videoTotal;
    [ObservableProperty] private long _followerTotal;
    [ObservableProperty] private long _yesterdayViews;
    [ObservableProperty] private string _weixinIncome = "0.00";
    [ObservableProperty] private string _kuaishouIncome = "0.00";

    partial void OnFromDateChanged(string value) => _ = RefreshAsync(false);
    partial void OnToDateChanged(string value) => _ = RefreshAsync(false);
    partial void OnSelectedPlatformChanged(string value) => _ = RefreshAsync(false);
    partial void OnKeywordChanged(string value) => _ = RefreshAsync(false);

    public async Task ActivateAsync()
    {
        await RefreshAsync(false);
        var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        if (_repository.GetState("auto-refresh-date") != today)
        {
            _repository.SetState("auto-refresh-date", today);
            await RefreshAsync(true);
        }
    }

    public void Export(string path)
    {
        var dataset = BuildDataset();
        AnalyticsCsvExporter.Export(path, dataset);
        StatusMessage = "数据统计已导出：" + path;
    }

    private async Task ImportLegacyAsync()
    {
        IsBusy=true; NotifyCommands();
        try
        {
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Yunfan");
            var report=await Task.Run(()=>_legacyImporter.Import(root,_accountProvider()));
            Apply(BuildDataset()); StatusMessage=report.Message;
        }
        catch(Exception ex){StatusMessage="历史数据导入失败："+ex.Message;}
        finally{IsBusy=false;NotifyCommands();}
    }

    private async Task RefreshAsync(bool collectOnline)
    {
        if (IsBusy) return;
        IsBusy = true; NotifyCommands();
        _cts = new CancellationTokenSource();
        try
        {
            var jobs = await _jobStore.LoadAsync(_cts.Token);
            _activitySync.Sync(jobs);
            var accounts = _accountProvider();
            if (collectOnline)
            {
                var yesterday = AnalyticsDatePolicy.Yesterday(DateTimeOffset.Now);
                foreach (var account in accounts)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    StatusMessage = "正在采集：" + account.Name;
                    try
                    {
                        if (account.Platform == PublishPlatform.WeixinChannel)
                        {
                            await _coordinator.RunExclusiveAsync(account.Id, async ct =>
                            {
                                var endpoint = await _weixinCdpProvider(account.Id, ct);
                                var snapshot = await _weixinCollector.CollectSnapshotAsync(endpoint, account.Id, ct);
                                _repository.UpsertSnapshot(snapshot);
                                _repository.UpsertDaily(new DailyAnalyticsRecord
                                {
                                    Platform = account.Platform, AccountId = account.Id, MetricDate = yesterday,
                                    CollectedAt = snapshot.CollectedAt, Status = AnalyticsRecordStatus.Success,
                                    AdMonetizationIncomeFen = snapshot.YesterdayAdMonetizationIncomeFen,
                                });
                                return true;
                            }, _cts.Token);
                        }
                        else if (account.Platform == PublishPlatform.KuaishouPersonalRevenue)
                        {
                            await _coordinator.RunExclusiveAsync(account.Id, async ct =>
                            {
                                _repository.UpsertSubjects(await _kuaishouCollector.CollectAsync(account, yesterday, ct));
                                return true;
                            }, _cts.Token);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        StatusMessage = $"{account.Name} 采集失败：{ex.Message}；继续使用已有缓存。";
                    }
                }
                _repository.SetState("last-refresh-at", DateTimeOffset.UtcNow.ToString("O"));
            }
            Apply(BuildDataset());
            StatusMessage = collectOnline ? "线上数据刷新完成。" : "已加载本地统计缓存。";
        }
        catch (OperationCanceledException) { StatusMessage = "数据统计刷新已停止。"; }
        finally { IsBusy = false; NotifyCommands(); _cts.Dispose(); _cts = null; }
    }

    private async Task BackfillAsync()
    {
        if (!TryDates(out var from, out var to)) return;
        var dates = AnalyticsDatePolicy.Range(from, to);
        var accounts = _accountProvider().Where(account => account.Platform == PublishPlatform.WeixinChannel).ToArray();
        IsBusy = true; NotifyCommands(); _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<AnalyticsCollectionProgress>(value => StatusMessage = $"{value.Current}/{value.Total} {value.Message}");
            await _coordinator.BackfillAsync(accounts, dates, async (account, date, ct) =>
            {
                var existing = _repository.ListDaily(date, date).Any(item => item.AccountId == account.Id && item.Status == AnalyticsRecordStatus.Success);
                if (existing) return;
                var endpoint = await _weixinCdpProvider(account.Id, ct);
                _repository.UpsertDaily(await _weixinCollector.CollectDailyAsync(endpoint, account.Id, date, ct));
            }, progress, _cts.Token);
            Apply(BuildDataset()); StatusMessage = "视频号日数据补采完成。";
        }
        catch (OperationCanceledException) { StatusMessage = "补采已停止，可再次执行以继续缺失日期。"; }
        catch (Exception ex) { StatusMessage = "补采失败：" + ex.Message; }
        finally { IsBusy = false; NotifyCommands(); _cts.Dispose(); _cts = null; }
    }

    private AnalyticsDataset BuildDataset()
    {
        if (!TryDates(out var from, out var to)) return _queryService.Query(_accountProvider(), DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today));
        PublishPlatform? platform = SelectedPlatform switch { "视频号" => PublishPlatform.WeixinChannel, "快手个人版" => PublishPlatform.KuaishouPersonalRevenue, _ => null };
        return _queryService.Query(_accountProvider(), from, to, platform, Keyword);
    }

    private bool TryDates(out DateOnly from, out DateOnly to)
    {
        from = default;
        to = default;
        if (DateOnly.TryParse(FromDate, out from) && DateOnly.TryParse(ToDate, out to) && from <= to) return true;
        StatusMessage = "统计日期格式无效，请使用 yyyy-MM-dd。"; return false;
    }

    private void Apply(AnalyticsDataset dataset)
    {
        AccountCount = dataset.Summary.AccountCount; TaskCount = dataset.Summary.TaskCount;
        SucceededCount = dataset.Summary.Succeeded; FailedCount = dataset.Summary.Failed;
        VideoTotal = dataset.Summary.VideoTotal; FollowerTotal = dataset.Summary.FollowerTotal;
        YesterdayViews = dataset.Summary.YesterdayViews;
        WeixinIncome = (dataset.Summary.WeixinIncomeFen / 100m).ToString("N2");
        KuaishouIncome = (dataset.Summary.KuaishouIncomeFen / 100m).ToString("N2");
        Replace(AccountRows, dataset.Accounts.Select(item => new AnalyticsAccountRowViewModel(item)));
        Replace(PlatformRows, dataset.Accounts.GroupBy(item => item.Account.Platform).Select(group => new AnalyticsPlatformRowViewModel(group.Key, group)));
        Replace(DailyRows, dataset.Daily.Select(item => new AnalyticsDailyRowViewModel(item)));
        Replace(IncomeRows, dataset.Accounts.Select(item => new AnalyticsIncomeRowViewModel(item)));
    }

    private void NotifyCommands() { RefreshCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged(); BackfillCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); ImportLegacyCommand.NotifyCanExecuteChanged(); }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
}

public sealed record AnalyticsAccountRowViewModel(AnalyticsAccountRow Row)
{
    public string Account => Row.Account.Name; public string Platform => Row.Account.Platform.DisplayName();
    public int Tasks => Row.TaskCount; public int Succeeded => Row.Succeeded; public int Failed => Row.Failed; public int Drafts => Row.Drafts;
    public string Videos => (Row.Snapshot?.VideoTotal ?? Row.KuaishouSummary?.Views)?.ToString("N0") ?? "—";
    public string Followers => Row.Snapshot?.FollowerTotal?.ToString("N0") ?? "—";
    public string YesterdayViews => (Row.Snapshot?.YesterdayViews ?? Row.KuaishouSummary?.Views)?.ToString("N0") ?? "—";
    public string Income => (((Row.Account.Platform == PublishPlatform.WeixinChannel ? Row.Snapshot?.AdMonetizationIncomeFen : Row.KuaishouSummary?.AdIncomeFen) ?? 0) / 100m).ToString("N2");
    public string CollectedAt => (Row.Snapshot?.CollectedAt ?? Row.KuaishouSummary?.CollectedAt)?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
}
public sealed record AnalyticsPlatformRowViewModel
{
    public AnalyticsPlatformRowViewModel(PublishPlatform platform, IEnumerable<AnalyticsAccountRow> rows) { var values=rows.ToArray(); Platform=platform.DisplayName();Accounts=values.Length;Tasks=values.Sum(x=>x.TaskCount);Succeeded=values.Sum(x=>x.Succeeded);Failed=values.Sum(x=>x.Failed);Income=(values.Sum(x=>(platform==PublishPlatform.WeixinChannel?x.Snapshot?.AdMonetizationIncomeFen:x.KuaishouSummary?.AdIncomeFen)??0)/100m).ToString("N2"); }
    public string Platform { get; } public int Accounts { get; } public int Tasks { get; } public int Succeeded { get; } public int Failed { get; } public string Income { get; }
}
public sealed record AnalyticsDailyRowViewModel(AnalyticsDailyPoint Point) { public string Date => Point.Date.ToString("MM-dd"); public int Succeeded=>Point.Succeeded; public int Failed=>Point.Failed; public int Drafts=>Point.Drafts; }
public sealed record AnalyticsIncomeRowViewModel(AnalyticsAccountRow Row) { public string Account=>Row.Account.Name; public string Platform=>Row.Account.Platform.DisplayName(); public string YesterdayIncome=>(((Row.Snapshot?.YesterdayAdMonetizationIncomeFen??Row.KuaishouSummary?.AdIncomeFen)??0)/100m).ToString("N2"); public string TotalIncome=>(((Row.Snapshot?.AdMonetizationIncomeFen??Row.KuaishouSummary?.AdIncomeFen)??0)/100m).ToString("N2"); public string Range=>Row.Snapshot is null?Row.KuaishouSummary?.MetricDate.ToString("yyyy-MM-dd")??"—":$"{Row.Snapshot.RangeStart} ~ {Row.Snapshot.RangeEnd}"; }
