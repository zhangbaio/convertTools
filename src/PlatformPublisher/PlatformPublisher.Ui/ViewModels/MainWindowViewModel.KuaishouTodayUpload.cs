using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;
using GlobalAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string LoadingKuaishouTodayUploadText = "今日已上传剧集：统计中…";
    private readonly KuaishouTodayUploadDisplay _kuaishouTodayUpload = new();
    private bool _showKuaishouSidebarUploadCounts;

    internal event Action? KuaishouTodayUploadCountsChanged;

    internal int? GetKuaishouTodayUploadCount(PublishPlatform platform, string accountId) =>
        _kuaishouTodayUpload.GetCount(platform, accountId, DateTime.Today);

    public void RefreshKuaishouTodayUploadCount() => PostUi(() =>
    {
        UpdateKuaishouTodayUploadText();
        if (IsKuaishouPlatform && _activeGlobalAccount is { } account)
            RefreshKuaishouTodayUploadCount(SelectedPlatform.Value, account);
    });

    internal void RefreshKuaishouSidebarUploadCounts()
    {
        _showKuaishouSidebarUploadCounts = true;
        RefreshKuaishouSidebarUploadCounts(DateTime.Today);
    }

    private void RefreshKuaishouSidebarUploadCounts(DateTime day)
    {
        if (_kuaishouTodayUpload.SetDay(day))
            NotifyKuaishouTodayUploadCountsChanged();

        var requests = new List<UploadCountQuery>();
        foreach (var account in _globalAccounts.OrderByDescending(account => account.Id == _activeGlobalAccount?.Id))
        {
            if (!string.IsNullOrWhiteSpace(account.Id) &&
                KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
                requests.Add(CreateUploadCountQuery(platform, account, day));
        }
        if (requests.Count == 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var counts = KuaishouTodayUploadCounter.CountRecordedByAccount(day);
                PostUi(() =>
                {
                    var updated = false;
                    foreach (var query in requests)
                    {
                        var request = query.Request;
                        var count = counts.GetValueOrDefault((request.Platform, query.Account.Id.Trim()));
                        updated |= _kuaishouTodayUpload.TryRecord(request, DateTime.Today, count);
                    }
                    if (updated)
                        NotifyKuaishouTodayUploadCountsChanged();
                });
            }
            catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"读取快手账号今日上传记录失败：{ex.Message}");
            }

            foreach (var query in requests)
                ReconcileKuaishouUploadCount(query, readRecordedCount: false);
        });
    }

    private void RefreshKuaishouTodayUploadCount(PublishPlatform platform, GlobalAccount account)
    {
        var day = DateTime.Today;
        if (_kuaishouTodayUpload.SetDay(day))
        {
            NotifyKuaishouTodayUploadCountsChanged();
            if (_showKuaishouSidebarUploadCounts)
            {
                RefreshKuaishouSidebarUploadCounts(day);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(account.Id))
            return;
        var query = CreateUploadCountQuery(platform, account, day);
        _ = Task.Run(() => ReconcileKuaishouUploadCount(query, readRecordedCount: true));
    }

    private void RefreshKuaishouUploadReport(PublishPlatform platform, string accountId) => PostUi(() =>
    {
        var account = _globalAccounts.FirstOrDefault(item =>
            string.Equals(item.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is not null)
            RefreshKuaishouTodayUploadCount(platform, account);
    });

    private UploadCountQuery CreateUploadCountQuery(PublishPlatform platform, GlobalAccount account, DateTime day)
    {
        var request = _kuaishouTodayUpload.Begin(platform, account.Id, day);
        var directories = _jobs
            .Where(job => job.Platform == platform &&
                          string.Equals(job.AccountId, account.Id, StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrWhiteSpace(job.ProjectDirectory))
            .Select(job => job.ProjectDirectory)
            .ToArray();
        return new UploadCountQuery(request, account, directories);
    }

    private void ReconcileKuaishouUploadCount(UploadCountQuery query, bool readRecordedCount)
    {
        var (request, account, directories) = query;
        if (request.Day != DateTime.Today || !_kuaishouTodayUpload.IsCurrent(request))
            return;

        var platform = request.Platform;
        var accountId = account.Id.Trim();
        try
        {
            if (readRecordedCount)
                PublishCount(KuaishouTodayUploadCounter.CountToday(platform, accountId, null, today: request.Day));

            if (!_kuaishouTodayUpload.IsCurrent(request))
                return;

            var (archiveRoot, nickname) = ReadKuaishouUploadCountSettings(new PublishJob
            {
                Platform = platform,
                AccountId = accountId,
                ConfigPath = ResolveGlobalConfigPath(account, platform),
            });
            PublishCount(KuaishouTodayUploadCounter.CountToday(
                platform, accountId, null, today: request.Day, projectDirectories: directories,
                archiveRoot: archiveRoot, nickname: nickname));
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"读取快手今日上传数量失败：{ex.Message}");
            PostUi(() =>
            {
                if (_kuaishouTodayUpload.IsCurrent(request) && IsKuaishouPlatform &&
                    SelectedPlatform.Value == platform &&
                    string.Equals(_activeGlobalAccount?.Id, accountId, StringComparison.OrdinalIgnoreCase) &&
                    KuaishouTodayUploadText == LoadingKuaishouTodayUploadText)
                    KuaishouTodayUploadText = "今日已上传剧集：暂未读取";
            });
        }

        void PublishCount(int count) => PostUi(() =>
        {
            if (_kuaishouTodayUpload.TryRecord(request, DateTime.Today, count))
                NotifyKuaishouTodayUploadCountsChanged();
        });
    }

    private void NotifyKuaishouTodayUploadCountsChanged()
    {
        UpdateKuaishouTodayUploadText();
        KuaishouTodayUploadCountsChanged?.Invoke();
    }

    private void UpdateKuaishouTodayUploadText()
    {
        if (!IsKuaishouPlatform || string.IsNullOrWhiteSpace(_activeGlobalAccount?.Id))
        {
            KuaishouTodayUploadText = "";
            return;
        }

        var count = GetKuaishouTodayUploadCount(SelectedPlatform.Value, _activeGlobalAccount.Id);
        KuaishouTodayUploadText = count is { } value
            ? $"今日已上传剧集：{value}"
            : LoadingKuaishouTodayUploadText;
    }

    private static (string? ArchiveRoot, string? Nickname) ReadKuaishouUploadCountSettings(PublishJob job)
    {
        try
        {
            return KuaishouPersonalConfig.ReadUploadCountSettings(job);
        }
        catch (Exception ex) when (ex is DbException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"读取快手今日上传统计配置失败：{ex.Message}");
            return (null, null);
        }
    }

    private sealed record UploadCountQuery(KuaishouTodayUploadDisplay.Request Request, GlobalAccount Account, string[] Directories);
}

public sealed class KuaishouTodayUploadDisplay
{
    public readonly record struct Request(PublishPlatform Platform, string AccountId, DateTime Day, int Revision)
    {
        internal (PublishPlatform Platform, string AccountId) Key => (Platform, AccountId);
    }

    private readonly ConcurrentDictionary<(PublishPlatform Platform, string AccountId), (int Revision, int? Count)> _counts = new();
    private DateTime _day;
    private int _revision;

    public bool SetDay(DateTime day)
    {
        if (_day == day.Date)
            return false;
        _counts.Clear();
        _day = day.Date;
        return true;
    }

    public Request Begin(PublishPlatform platform, string accountId, DateTime day)
    {
        SetDay(day);
        var request = new Request(platform, accountId.Trim().ToUpperInvariant(), day.Date, ++_revision);
        var count = _counts.GetValueOrDefault(request.Key).Count;
        _counts[request.Key] = (request.Revision, count);
        return request;
    }

    public int? GetCount(PublishPlatform platform, string accountId, DateTime day) =>
        _day == day.Date ? _counts.GetValueOrDefault((platform, accountId.Trim().ToUpperInvariant())).Count : null;

    public bool IsCurrent(Request request) =>
        _counts.TryGetValue(request.Key, out var entry) && entry.Revision == request.Revision;

    public bool TryRecord(Request request, DateTime today, int count)
    {
        if (request.Day != today.Date || !IsCurrent(request))
            return false;

        _counts[request.Key] = (request.Revision, count);
        return true;
    }
}
