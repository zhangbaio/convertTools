using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Remote;

namespace TikTokPublisher.Core.Services;

public enum XingeAutoPublishSendOutcome
{
    Delivered,
    Rejected,
    Retry,
}

/// <summary>
/// 自动上新派单进度回传。先落盘到发件箱再发送：网络中断、XINGE 重启或客户端重启后都会继续补发，
/// 终态（success/failed）不会因为一次请求失败而丢失。
/// </summary>
public sealed class XingeAutoPublishProgressReporter
{
    public const string DefaultProgressPathPrefix = "/client-api/tiktok/auto-publish/assignments/";
    private const int MaxDetailLength = 500;
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
    private List<XingeAutoPublishProgress>? _pending;

    public XingeAutoPublishProgressReporter(
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

    public static XingeAutoPublishProgressReporter Shared { get; } = new(
        new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
        Path.Combine(AppPaths.DataRoot, "xinge-auto-publish-outbox.json"),
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
    public void Enqueue(XingeAutoPublishProgress progress)
    {
        Add(progress);
        _ = Task.Run(async () =>
        {
            try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log?.Invoke($"XINGE 自动上新进度回传失败：{ex.Message}"); }
        });
    }

    /// <summary>入队并立即发送；返回该条是否已被服务端处理（送达或明确拒收）。</summary>
    public async Task<bool> ReportAsync(XingeAutoPublishProgress progress, CancellationToken ct)
    {
        Add(progress);
        try
        {
            await FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke($"XINGE 自动上新进度回传失败，稍后重试：{ex.Message}");
        }

        lock (_sync)
            return !LoadPending().Contains(progress);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                XingeAutoPublishProgress? next;
                lock (_sync)
                {
                    var pending = LoadPending();
                    var expired = pending.RemoveAll(item => _now() - item.CreatedAt > MaxPendingAge);
                    if (expired > 0)
                        SavePending();
                    next = pending.FirstOrDefault();
                }

                if (next is null)
                    return;

                var outcome = await SendAsync(next, ct).ConfigureAwait(false);
                if (outcome == XingeAutoPublishSendOutcome.Retry)
                    return;

                lock (_sync)
                {
                    LoadPending().Remove(next);
                    SavePending();
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void Add(XingeAutoPublishProgress progress)
    {
        lock (_sync)
        {
            var pending = LoadPending();
            var sameAssignment = pending.Where(item => item.AssignmentId == progress.AssignmentId).ToList();
            if (!XingeAutoPublishStatuses.IsTerminal(progress.Status) &&
                sameAssignment.Any(item => XingeAutoPublishStatuses.IsTerminal(item.Status)))
            {
                return;
            }

            // 未发出的中间进度被新状态取代，只保留最新一条，避免断网恢复后补发大量过期步骤。
            pending.RemoveAll(item =>
                item.AssignmentId == progress.AssignmentId &&
                (!XingeAutoPublishStatuses.IsTerminal(item.Status) || XingeAutoPublishStatuses.IsTerminal(progress.Status)));
            pending.Add(progress);
            SavePending();
        }
    }

    private async Task<XingeAutoPublishSendOutcome> SendAsync(XingeAutoPublishProgress progress, CancellationToken ct)
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
            ["status"] = progress.Status,
            ["step"] = progress.Step,
            ["detail"] = Truncate(progress.Detail, MaxDetailLength),
            ["failure_kind"] = progress.FailureKind,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + ResolveProgressPath(progress))
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
            Log?.Invoke($"XINGE 自动上新进度回传暂不可达，稍后重试：{ex.Message}");
            return XingeAutoPublishSendOutcome.Retry;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return XingeAutoPublishSendOutcome.Delivered;

            var status = (int)response.StatusCode;
            // 认证失败可能是凭据正在重新签发；5xx 是服务端临时故障；其余 4xx 表示派单已不存在或请求非法，重发无意义。
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ||
                status >= 500)
            {
                return XingeAutoPublishSendOutcome.Retry;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log?.Invoke($"XINGE 拒收派单 {progress.AssignmentId} 的进度（HTTP {status}）：{Truncate(text, 200)}");
            return XingeAutoPublishSendOutcome.Rejected;
        }
    }

    // 只允许回传到自动上新进度接口，防止命令载荷把客户端凭据引向其他路径。
    internal static string ResolveProgressPath(XingeAutoPublishProgress progress)
    {
        var path = (progress.ProgressPath ?? "").Trim();
        var expected = $"{DefaultProgressPathPrefix}{progress.AssignmentId}/progress";
        return string.Equals(path, expected, StringComparison.Ordinal) ? path : expected;
    }

    private List<XingeAutoPublishProgress> LoadPending()
    {
        if (_pending is not null)
            return _pending;
        try
        {
            _pending = File.Exists(_outboxPath)
                ? JsonSerializer.Deserialize<List<XingeAutoPublishProgress>>(File.ReadAllText(_outboxPath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log?.Invoke($"XINGE 自动上新发件箱读取失败，已重置：{ex.Message}");
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
            Log?.Invoke($"XINGE 自动上新发件箱保存失败：{ex.Message}");
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
