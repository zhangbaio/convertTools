using System.Collections.Concurrent;
using PlatformPublisher.Analytics.Models;

namespace PlatformPublisher.Analytics.Services;

public sealed record AnalyticsCollectionProgress(
    string AccountId,
    string AccountName,
    DateOnly? MetricDate,
    int Current,
    int Total,
    AnalyticsRecordStatus Status,
    string Message);

public sealed class AnalyticsCollectionCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> RunExclusiveAsync<T>(string accountId, Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var gate = _accountGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("该账号已有数据采集或浏览器任务正在运行。");
        try { return await action(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task RunExclusiveAsync(string accountId, Func<CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        await RunExclusiveAsync(accountId, async ct => { await action(ct); return true; }, cancellationToken);

    public async Task BackfillAsync(
        IReadOnlyList<AnalyticsAccount> accounts,
        IReadOnlyList<DateOnly> dates,
        Func<AnalyticsAccount, DateOnly, CancellationToken, Task> collect,
        IProgress<AnalyticsCollectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = accounts.Count * dates.Count;
        var current = 0;
        foreach (var account in accounts)
        {
            foreach (var date in dates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(account.Id, account.Name, date, current, total, AnalyticsRecordStatus.Missing,
                    $"正在采集 {account.Name} · {date:yyyy-MM-dd}"));
                try
                {
                    await RunExclusiveAsync(account.Id, ct => collect(account, date, ct), cancellationToken);
                    current++;
                    progress?.Report(new(account.Id, account.Name, date, current, total, AnalyticsRecordStatus.Success,
                        $"已完成 {account.Name} · {date:yyyy-MM-dd}"));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    current++;
                    progress?.Report(new(account.Id, account.Name, date, current, total, AnalyticsRecordStatus.Failed,
                        $"{account.Name} · {date:yyyy-MM-dd}：{ex.Message}"));
                }
            }
        }
    }
}
