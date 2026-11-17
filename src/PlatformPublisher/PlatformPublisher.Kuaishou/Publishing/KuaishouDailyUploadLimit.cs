using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouDailyUploadLimit
{
    public const string FailureKind = "account_daily_limit";
    public const string Detail = "快手分账今日新建短剧已达 10 部。还没有草稿的剧今天不再创建，上传失败的草稿可以继续。";
    private static readonly AsyncLocal<string?> Account = new();
    private static readonly object NotedGate = new();
    private static readonly Dictionary<string, DateOnly> NotedDays = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CreateGates = new(StringComparer.OrdinalIgnoreCase);

    public static string CurrentAccountId => Account.Value?.Trim() ?? "";

    public static IDisposable UseAccount(string? accountId) => new AccountScope(accountId);

    public static bool IsNoted(string? accountId)
    {
        var id = (accountId ?? "").Trim();
        if (id.Length == 0)
            return false;
        lock (NotedGate)
            return NotedDays.TryGetValue(id, out var day) && day == DateOnly.FromDateTime(DateTime.Now);
    }

    public static bool TryNote(string? accountId)
    {
        var id = (accountId ?? "").Trim();
        if (id.Length == 0)
            return false;
        var today = DateOnly.FromDateTime(DateTime.Now);
        lock (NotedGate)
        {
            if (NotedDays.TryGetValue(id, out var day) && day == today)
                return false;
            NotedDays[id] = today;
            return true;
        }
    }

    public static void NoteCurrent() => TryNote(Account.Value);

    public static void ThrowIfNoted(string? accountId)
    {
        if (IsNoted(accountId))
            throw new KuaishouDailyUploadLimitException();
    }

    public static SemaphoreSlim CreateGateFor(string? accountId)
    {
        var id = (accountId ?? "").Trim();
        if (id.Length == 0)
            id = CurrentAccountId;
        if (id.Length == 0)
            id = "_";
        return CreateGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    }

    public static bool HasStartedUpload(KuaishouPersonalUploadState? state) =>
        state is not null &&
        !string.IsNullOrWhiteSpace(state.MiniSeriesId) &&
        state.FirstPageCompleted;

    private static readonly string[] VisiblePhrases =
    [
        "您今日的短剧上传已达上限",
        "今日上传额度已用完",
        "每天最多上传10部",
        "每天最多上传 10 部",
    ];

    public static bool Matches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var compact = text.Replace(" ", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
        return compact.Contains("短剧上传已达上限", StringComparison.Ordinal)
            || compact.Contains("今日上传额度已用完", StringComparison.Ordinal)
            || compact.Contains("每天最多上传10部", StringComparison.Ordinal)
            || compact.Contains("每日限传10部", StringComparison.Ordinal);
    }

    public static bool AllowsResume(KuaishouPersonalUploadState? state)
    {
        if (state is null || state.SkipTodayUploadCount || state.VideosUploaded || state.ReviewSubmitted)
            return false;
        var status = state.Status?.Trim() ?? string.Empty;
        if (status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("success", StringComparison.OrdinalIgnoreCase))
            return false;
        return !string.IsNullOrWhiteSpace(state.MiniSeriesId) && state.FirstPageCompleted;
    }

    public static bool Is(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is KuaishouDailyUploadLimitException || Matches(current.Message))
                return true;
        }

        return false;
    }

    public static async Task<bool> IsVisibleAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            foreach (var phrase in VisiblePhrases)
            {
                try
                {
                    var hit = frame.GetByText(phrase, new FrameGetByTextOptions { Exact = false }).First;
                    if (await hit.IsVisibleAsync())
                        return true;
                }
                catch (PlaywrightException)
                {
                    // 框架切换或节点尚未挂上时继续看下一处。
                }
            }
        }

        return false;
    }

    private sealed class AccountScope : IDisposable
    {
        private readonly string? _previous;

        public AccountScope(string? accountId)
        {
            _previous = Account.Value;
            Account.Value = accountId?.Trim() ?? "";
        }

        public void Dispose() => Account.Value = _previous;
    }
}

public sealed class KuaishouDailyUploadLimitException : InvalidOperationException
{
    public KuaishouDailyUploadLimitException()
        : base(KuaishouDailyUploadLimit.Detail)
    {
    }
}
