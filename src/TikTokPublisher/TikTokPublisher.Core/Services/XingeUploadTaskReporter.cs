using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Remote;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 按剧上报上传任务状态到 XINGE 监控屏。先落盘到发件箱再批量发送：断网、XINGE 重启或客户端重启后继续补发；
/// 同一部剧未发出的中间状态被新状态取代，终态不会被迟到的中间状态覆盖。
/// </summary>
public sealed class XingeUploadTaskReporter
{
    public const string ReportPath = "/client-api/tiktok/upload-tasks/report";
    private const int BatchSize = 50;
    private const int MaxPending = 2000;
    private const int MaxReasonLength = 500;
    private static readonly TimeSpan MaxPendingAge = TimeSpan.FromDays(3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly string _outboxPath;
    private readonly Func<ClientSettings> _settingsProvider;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private List<XingeUploadTaskReport>? _pending;

    public XingeUploadTaskReporter(
        HttpClient http,
        string outboxPath,
        Func<ClientSettings> settingsProvider,
        Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _outboxPath = outboxPath;
        _settingsProvider = settingsProvider;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static XingeUploadTaskReporter Shared { get; } = new(
        new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
        Path.Combine(AppPaths.DataRoot, "xinge-upload-task-outbox.json"),
        () => ClientSettingsStore.Load());

    public event Action<string>? Log;

    public int PendingCount
    {
        get
        {
            lock (_sync)
                return LoadPending().Count;
        }
    }

    /// <summary>入队并在后台尝试发送，不阻塞调用方（队列进度回调使用）。</summary>
    public void Enqueue(XingeUploadTaskReport report)
    {
        Add(report);
        _ = Task.Run(async () =>
        {
            try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log?.Invoke($"XINGE 上传任务上报失败：{ex.Message}"); }
        });
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                List<XingeUploadTaskReport> batch;
                lock (_sync)
                {
                    var pending = LoadPending();
                    if (pending.RemoveAll(item => _now() - item.OccurredAt > MaxPendingAge) > 0)
                        SavePending();
                    batch = pending.Take(BatchSize).ToList();
                }

                if (batch.Count == 0)
                    return;

                if (await SendAsync(batch, ct).ConfigureAwait(false) == XingeAutoPublishSendOutcome.Retry)
                    return;

                lock (_sync)
                {
                    var pending = LoadPending();
                    foreach (var sent in batch)
                        pending.Remove(sent);
                    SavePending();
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void Add(XingeUploadTaskReport report)
    {
        lock (_sync)
        {
            var pending = LoadPending();
            // 同一部剧尚未发出的中间状态已过时；已有的终态保留，按时间顺序发送。
            pending.RemoveAll(item =>
                string.Equals(item.ProjectPath, report.ProjectPath, StringComparison.OrdinalIgnoreCase) &&
                !XingeUploadTaskStatuses.IsTerminal(item.Status));
            pending.Add(report);
            if (pending.Count > MaxPending)
            {
                var dropped = pending.Count - MaxPending;
                pending.RemoveRange(0, dropped);
                Log?.Invoke($"XINGE 上传任务发件箱已满，丢弃最早的 {dropped} 条");
            }
            SavePending();
        }
    }

    private async Task<XingeAutoPublishSendOutcome> SendAsync(IReadOnlyList<XingeUploadTaskReport> batch, CancellationToken ct)
    {
        var settings = _settingsProvider();
        var baseUrl = NormalizeBaseUrl(FirstNonEmpty(settings.XingeServerUrl, settings.AuthServerUrl));
        var clientId = settings.XingeClientId?.Trim() ?? "";
        var clientToken = settings.XingeClientToken?.Trim() ?? "";
        if (!settings.XingeRemoteEnabled || baseUrl.Length == 0 || clientId.Length == 0 || clientToken.Length == 0)
            return XingeAutoPublishSendOutcome.Retry;

        var body = new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_token"] = clientToken,
            ["tasks"] = batch.Select(ToPayload).ToList(),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + ReportPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Remote-Client-ID", clientId);
        request.Headers.TryAddWithoutValidation("X-Remote-Client-Token", clientToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log?.Invoke($"XINGE 上传任务上报暂不可达，稍后重试：{ex.Message}");
            return XingeAutoPublishSendOutcome.Retry;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return XingeAutoPublishSendOutcome.Delivered;

            var status = (int)response.StatusCode;
            // 404 说明服务端尚未升级到支持监控屏，保留到升级后补发（发件箱有时效与容量上限）。
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.NotFound ||
                status >= 500)
            {
                return XingeAutoPublishSendOutcome.Retry;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log?.Invoke($"XINGE 拒收上传任务上报（HTTP {status}）：{Truncate(text, 200)}");
            return XingeAutoPublishSendOutcome.Rejected;
        }
    }

    private static Dictionary<string, object?> ToPayload(XingeUploadTaskReport report) => new()
    {
        ["project_path"] = report.ProjectPath,
        ["account_profile_id"] = report.AccountProfileId,
        ["account_profile_name"] = report.AccountProfileName,
        ["title"] = report.Title,
        ["new_title"] = report.NewTitle,
        ["episodes"] = report.Episodes,
        ["series_id"] = report.SeriesId,
        ["assignment_id"] = report.AssignmentId,
        ["status"] = report.Status,
        ["failure_kind"] = report.FailureKind,
        ["failure_reason"] = Truncate(report.FailureReason, MaxReasonLength),
        ["occurred_at"] = report.OccurredAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
    };

    private List<XingeUploadTaskReport> LoadPending()
    {
        if (_pending is not null)
            return _pending;
        try
        {
            _pending = File.Exists(_outboxPath)
                ? JsonSerializer.Deserialize<List<XingeUploadTaskReport>>(File.ReadAllText(_outboxPath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log?.Invoke($"XINGE 上传任务发件箱读取失败，已重置：{ex.Message}");
            _pending = [];
        }

        return _pending;
    }

    private void SavePending()
    {
        if (_pending is null)
            return;
        try
        {
            var dir = Path.GetDirectoryName(_outboxPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var temp = _outboxPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_pending, JsonOptions));
            File.Move(temp, _outboxPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"XINGE 上传任务发件箱保存失败：{ex.Message}");
        }
    }

    private static string NormalizeBaseUrl(string value)
    {
        var text = value.Trim().TrimEnd('/');
        if (text.Length == 0)
            return "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out _))
            text = "http://" + text;
        return text.TrimEnd('/');
    }

    private static string Truncate(string? value, int max)
    {
        var text = value ?? "";
        return text.Length > max ? text[..max] : text;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "").FirstOrDefault(value => value.Length > 0) ?? "";
}
