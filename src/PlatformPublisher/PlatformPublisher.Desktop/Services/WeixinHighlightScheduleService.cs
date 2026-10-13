using Avalonia.Threading;
using ChannelsPublisher.Core.Services;
using PlatformPublisher.Materials;
using PlatformPublisher.Persistence;
using PlatformPublisher.Publishing.Execution;
using PlatformPublisher.Publishing.Models;

namespace PlatformPublisher.Desktop.Services;

public sealed class WeixinHighlightScheduleService : IDisposable
{
    private const string RulesKey = "weixin.material.highlight.schedule.rules";
    private const string StatesKey = "weixin.material.highlight.schedule.states";
    private readonly IJsonSettingStore _settings;
    private readonly AccountStore _accounts;
    private readonly MaterialDraftFactory _draftFactory;
    private readonly PublishBatchCoordinator _coordinator;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _checking;

    public WeixinHighlightScheduleService(IJsonSettingStore settings, AccountStore accounts,
        MaterialDraftFactory draftFactory, PublishBatchCoordinator coordinator)
    {
        _settings = settings;
        _accounts = accounts;
        _draftFactory = draftFactory;
        _coordinator = coordinator;
        _timer.Tick += OnTick;
    }

    public event Action<string>? StatusChanged;

    public IReadOnlyList<WeixinHighlightScheduleRule> LoadRules() =>
        _settings.Load(RulesKey, static () => new List<WeixinHighlightScheduleRule>());

    public void SaveRules(IEnumerable<WeixinHighlightScheduleRule> rules) =>
        _settings.Save(RulesKey, rules.Select(Normalize).ToList());

    public void Start()
    {
        if (!_timer.IsEnabled) _timer.Start();
        _ = CheckDueAsync(startup: true);
    }

    public async Task RunNowAsync(string ruleId, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var rule = LoadRules().FirstOrDefault(item => item.Id == ruleId)
                   ?? throw new InvalidOperationException("未找到系统高光自动发布规则。");
        await ExecuteAsync(rule, progress, cancellationToken);
    }

    private async void OnTick(object? sender, EventArgs e) => await CheckDueAsync(startup: false);

    private async Task CheckDueAsync(bool startup)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var now = DateTimeOffset.Now;
            var states = LoadStates();
            foreach (var rule in LoadRules().Where(item => item.Enabled))
            {
                states.TryGetValue(rule.Id, out var state);
                if (!IsDue(rule, state, now, startup)) continue;
                try
                {
                    await ExecuteAsync(rule, null, CancellationToken.None);
                    states[rule.Id] = new WeixinHighlightScheduleState(now, "执行完成");
                }
                catch (Exception ex)
                {
                    states[rule.Id] = new WeixinHighlightScheduleState(now, ex.Message);
                    StatusChanged?.Invoke($"系统高光定时任务“{rule.Name}”失败：{ex.Message}");
                }
                SaveStates(states);
            }
        }
        finally { _checking = false; }
    }

    private async Task ExecuteAsync(WeixinHighlightScheduleRule rawRule, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var rule = Normalize(rawRule);
        var account = _accounts.Accounts.FirstOrDefault(item =>
            string.Equals(item.Id, rule.AccountId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"规则账号已不存在：{rule.AccountId}");
        var titles = ParseLines(rule.TitlesText);
        if (titles.Count == 0) throw new InvalidOperationException("规则没有配置剧名。");
        foreach (var title in titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"系统高光自动发布：正在处理《{title}》");
            var source = new MaterialSourceSpec
            {
                Kind = MaterialSourceKind.SystemHighlight,
                Label = "系统高光自动发布",
                WorkflowDirectory = Directory.Exists(rule.WorkspaceDirectory) ? rule.WorkspaceDirectory : Path.GetTempPath(),
                OriginalTitle = title,
                NewTitle = title,
                PayloadJson = $"{{\"count\":{rule.PublishCount},\"videoTypes\":\"{rule.VideoTypes}\"}}",
            };
            var form = new UnifiedPublishForm
            {
                OriginalTitle = title,
                NewTitle = title,
                SeriesName = title,
                LinkSeries = true,
                LinkSeriesName = title,
                FillDescription = true,
                DescriptionTemplate = rule.Description,
                DeclareOriginal = true,
                FinalAction = UnifiedFinalAction.Publish,
                StopOnError = true,
            };
            var draft = await _draftFactory.CreateAsync(source, form, new MediaProcessingProfile(), cancellationToken);
            var request = new PublishBatchRequest
            {
                Draft = draft,
                Targets = [new PublishTarget(account.Id, account.Name, account.ProfileDir, 0)],
                DistributionMode = MaterialDistributionMode.Broadcast,
                FailurePolicy = PublishFailurePolicy.StopAll,
                MaxParallelAccounts = 1,
            };
            var outcome = await _coordinator.ExecuteAsync(request, null, cancellationToken);
            if (outcome.Status is not UnifiedPublishItemStatus.Success and not UnifiedPublishItemStatus.DraftSaved)
                throw new InvalidOperationException(outcome.Message);
        }
        progress?.Report($"系统高光自动发布完成：{titles.Count} 部剧。");
        StatusChanged?.Invoke($"系统高光自动发布“{rule.Name}”完成：{titles.Count} 部剧。");
    }

    internal static bool IsDue(WeixinHighlightScheduleRule rule, WeixinHighlightScheduleState? state,
        DateTimeOffset now, bool startup)
    {
        if (rule.TriggerMode == "interval")
            return state is null || now - state.LastRunAt >= TimeSpan.FromMinutes(rule.IntervalMinutes);
        if (!TimeOnly.TryParse(rule.ScheduleTime, out var time)) return false;
        if (rule.ScheduleMode == "weekly" && !ParseWeekdays(rule.Weekdays).Contains((int)now.DayOfWeek)) return false;
        var planned = now.Date.Add(time.ToTimeSpan());
        if (now < planned) return false;
        if (state?.LastRunAt.LocalDateTime.Date == now.Date) return false;
        return !startup || rule.CatchUpOnStartup || now - planned < TimeSpan.FromMinutes(1);
    }

    private Dictionary<string, WeixinHighlightScheduleState> LoadStates() =>
        _settings.Load(StatesKey, static () => new Dictionary<string, WeixinHighlightScheduleState>(StringComparer.OrdinalIgnoreCase));
    private void SaveStates(Dictionary<string, WeixinHighlightScheduleState> states) => _settings.Save(StatesKey, states);

    public static WeixinHighlightScheduleRule Normalize(WeixinHighlightScheduleRule rule) => rule with
    {
        Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id,
        Name = string.IsNullOrWhiteSpace(rule.Name) ? "系统高光自动发布" : rule.Name.Trim(),
        IntervalMinutes = Math.Clamp(rule.IntervalMinutes, 1, 1440),
        PublishCount = Math.Clamp(rule.PublishCount, 1, 100),
        ScheduleTime = TimeOnly.TryParse(rule.ScheduleTime, out _) ? rule.ScheduleTime : "09:00",
        VideoTypes = string.IsNullOrWhiteSpace(rule.VideoTypes) ? "混剪,解说,切片" : rule.VideoTypes,
    };

    private static IReadOnlyList<string> ParseLines(string text) => text.Split(['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
    private static HashSet<int> ParseWeekdays(string text) => text.Split([',', '，'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => int.TryParse(value, out var day) ? day : -1).Where(day => day is >= 0 and <= 6).ToHashSet();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}

public sealed record WeixinHighlightScheduleRule(string Id, string Name, bool Enabled, string AccountId,
    string WorkspaceDirectory, string TriggerMode, int IntervalMinutes, string ScheduleMode,
    string ScheduleTime, string Weekdays, bool CatchUpOnStartup, string TitlesText, int PublishCount,
    string VideoTypes, string Description)
{
    public static WeixinHighlightScheduleRule Create(string accountId, string workspace) => new(
        string.Empty, "系统高光自动发布", true, accountId, workspace, "fixed_time", 30, "daily",
        "09:00", "0,1,2,3,4,5,6", false, string.Empty, 10, "混剪,解说,切片",
        "热播爆火剧，点击链接，免费观看全集。热门#爆火");
}

public sealed record WeixinHighlightScheduleState(DateTimeOffset LastRunAt, string Summary);
