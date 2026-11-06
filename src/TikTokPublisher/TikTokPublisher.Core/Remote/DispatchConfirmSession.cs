namespace TikTokPublisher.Core.Remote;

public enum DispatchConfirmDecision
{
    Execute,
    RejectOnce,
    RejectToday,
}

public enum DispatchConfirmOffer
{
    Opened,
    Appended,
    PendingDecision,
}

public sealed class DispatchConfirmItem
{
    public long MessageId { get; init; }
    public long AssignmentId { get; init; }
    public string AccountName { get; init; } = "";
    public string Edition { get; init; } = "";
    public string Title { get; init; } = "";
    public int ConfirmSeconds { get; init; }
}

public sealed class DispatchConfirmSession
{
    public const int DefaultCountdownSeconds = 60;
    private readonly object _gate = new();
    private readonly List<DispatchConfirmItem> _items = [];

    public DispatchConfirmSession(DateTimeOffset startedAt, int countdownSeconds)
    {
        StartedAt = startedAt;
        Countdown = TimeSpan.FromSeconds(NormalizeCountdownSeconds(countdownSeconds));
        Deadline = startedAt + Countdown;
        Decision = new TaskCompletionSource<DispatchConfirmDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static int NormalizeCountdownSeconds(int seconds) =>
        seconds is >= 5 and <= 600 ? seconds : DefaultCountdownSeconds;

    public DateTimeOffset StartedAt { get; }
    public TimeSpan Countdown { get; }
    public DateTimeOffset Deadline { get; }
    public TaskCompletionSource<DispatchConfirmDecision> Decision { get; }

    public bool TryAdd(DispatchConfirmItem item)
    {
        lock (_gate)
        {
            if (_items.Any(existing => existing.MessageId == item.MessageId))
                return false;
            _items.Add(item);
            return true;
        }
    }

    public IReadOnlyList<DispatchConfirmItem> Snapshot()
    {
        lock (_gate)
            return _items.ToArray();
    }

    public int RemainingSeconds(DateTimeOffset now)
    {
        var left = (int)Math.Ceiling((Deadline - now).TotalSeconds);
        return left < 0 ? 0 : left;
    }

    public bool IsExpired(DateTimeOffset now) => now >= Deadline;

    public string BodyText()
    {
        var items = Snapshot();
        var groups = new List<(string Header, List<string> Titles)>();
        foreach (var item in items)
        {
            var header = AccountHeader(item);
            var group = groups.FirstOrDefault(entry => entry.Header == header);
            if (group.Header is null)
            {
                group = (header, []);
                groups.Add(group);
            }
            var title = string.IsNullOrWhiteSpace(item.Title) ? "未命名剧集" : item.Title.Trim();
            if (!group.Titles.Contains(title, StringComparer.Ordinal))
                group.Titles.Add(title);
        }

        var accounts = groups.Count;
        var dramas = groups.Sum(group => group.Titles.Count);
        var lines = new List<string> { $"共 {accounts} 个账号，{dramas} 部剧" };
        foreach (var group in groups)
        {
            lines.Add("");
            lines.Add($"{group.Header}（{group.Titles.Count} 部）");
            lines.AddRange(group.Titles.Select(title => "· " + title));
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string EditionLabel(string? edition) =>
        (edition ?? "").Trim().ToLowerInvariant() switch
        {
            "personal" => "个人版",
            "enterprise" => "企业版",
            _ => "",
        };

    private static string AccountHeader(DispatchConfirmItem item)
    {
        var name = string.IsNullOrWhiteSpace(item.AccountName) ? "未命名账号" : item.AccountName.Trim();
        var edition = EditionLabel(item.Edition);
        return edition.Length == 0 ? name : $"{name} · {edition}";
    }
}

public sealed class DispatchConfirmCoordinator
{
    private readonly object _gate = new();
    private DispatchConfirmSession? _session;

    public Task? DecisionTask
    {
        get
        {
            lock (_gate)
                return _session?.Decision.Task;
        }
    }

    public DispatchConfirmOffer Offer(DispatchConfirmItem item, DateTimeOffset now, out DispatchConfirmSession? session)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                if (!_session.Decision.Task.IsCompleted)
                {
                    _session.TryAdd(item);
                    session = _session;
                    return DispatchConfirmOffer.Appended;
                }

                session = null;
                return DispatchConfirmOffer.PendingDecision;
            }

            var opened = new DispatchConfirmSession(now, item.ConfirmSeconds);
            opened.TryAdd(item);
            _session = opened;
            session = opened;
            return DispatchConfirmOffer.Opened;
        }
    }

    public bool ExpireIfDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_session is null || _session.Decision.Task.IsCompleted || !_session.IsExpired(now))
                return false;
        }

        DispatchConfirmSession? session;
        lock (_gate)
            session = _session;
        return session is not null && session.Decision.TrySetResult(DispatchConfirmDecision.Execute);
    }

    public bool TryConsume(out DispatchConfirmDecision decision, out IReadOnlyList<DispatchConfirmItem> items)
    {
        lock (_gate)
        {
            if (_session is null || !_session.Decision.Task.IsCompleted)
            {
                decision = default;
                items = [];
                return false;
            }

            decision = _session.Decision.Task.Result;
            items = _session.Snapshot();
            _session = null;
            return true;
        }
    }
}
