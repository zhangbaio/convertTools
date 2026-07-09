using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services;

public sealed record XingeRemoteRegistrationSnapshot(
    string WorkspacePath,
    string ActiveAccountProfileId,
    IReadOnlyList<XingeRemoteAccountSnapshot> AccountProfiles);

public sealed record XingeRemoteAccountSnapshot(
    string Id,
    string Name,
    bool AuthReady,
    bool IsCurrent);

public sealed class XingeRemoteCommandService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public event Action<string>? StatusChanged;

    public void Restart(
        Func<TikTokRemoteCommand, Task<TikTokRemoteCommandResult>> executeAsync,
        Func<XingeRemoteRegistrationSnapshot> snapshotProvider,
        Action<string>? log = null)
    {
        Stop();

        var settings = ClientSettingsStore.Load();
        if (!settings.XingeRemoteEnabled)
        {
            Notify("XINGE 未启用");
            return;
        }

        var config = XingeRemoteConfig.FromSettings(settings);
        if (!config.IsReady)
        {
            Notify("XINGE 未配置客户端 ID/Token 或服务地址");
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _cts = cts;
            _worker = Task.Run(() => RunAsync(config, executeAsync, snapshotProvider, log, cts.Token));
        }

        Notify("XINGE 正在连接...");
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_sync)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }

        try { cts?.Cancel(); }
        catch { }
        if (cts is null)
            return;

        if (worker is null)
        {
            cts.Dispose();
            return;
        }

        _ = worker.ContinueWith(
            _ => cts.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunAsync(
        XingeRemoteConfig config,
        Func<TikTokRemoteCommand, Task<TikTokRemoteCommandResult>> executeAsync,
        Func<XingeRemoteRegistrationSnapshot> snapshotProvider,
        Action<string>? log,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        var lastRegisteredAt = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastRegisteredAt >= TimeSpan.FromSeconds(30))
                {
                    var snapshot = snapshotProvider();
                    await RegisterAsync(http, config, snapshot, ct).ConfigureAwait(false);
                    lastRegisteredAt = DateTimeOffset.UtcNow;
                    Notify($"XINGE 已在线：{snapshot.AccountProfiles.Count} 个账号");
                }

                var message = await PollAsync(http, config, ct).ConfigureAwait(false);
                if (message is not null)
                {
                    await HandleCommandAsync(http, config, message, executeAsync, log, ct).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(config.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var text = $"XINGE 远程错误：{ex.Message}";
                Notify(text);
                log?.Invoke(text);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private static async Task RegisterAsync(
        HttpClient http,
        XingeRemoteConfig config,
        XingeRemoteRegistrationSnapshot snapshot,
        CancellationToken ct)
    {
        var version = typeof(XingeRemoteCommandService).Assembly.GetName().Version?.ToString() ?? "unknown";
        var body = new
        {
            client_id = config.ClientId,
            client_token = config.ClientToken,
            machine_id = Environment.MachineName,
            device_name = FirstNonEmpty(config.ClientName, $"TikTokPublisher-{Environment.MachineName}"),
            app_version = $"TikTokPublisher/{version}",
            workspace_path = snapshot.WorkspacePath,
            active_account_profile_id = snapshot.ActiveAccountProfileId,
            account_profiles = snapshot.AccountProfiles.Select(account => new
            {
                id = account.Id,
                name = account.Name,
                auth_ready = account.AuthReady,
                is_current = account.IsCurrent,
            }).ToArray(),
        };

        var envelope = await SendAsync<XingeApiEnvelope<JsonElement>>(
            http,
            config,
            HttpMethod.Post,
            "/client-api/remote/register",
            body,
            ct).ConfigureAwait(false);

        if (!envelope.Ok)
            throw new InvalidOperationException(FirstNonEmpty(envelope.Message, "register failed"));
    }

    private static async Task<XingeRemoteMessage?> PollAsync(
        HttpClient http,
        XingeRemoteConfig config,
        CancellationToken ct)
    {
        var envelope = await SendAsync<XingeApiEnvelope<XingeRemoteMessage>>(
            http,
            config,
            HttpMethod.Get,
            "/client-api/remote/poll",
            null,
            ct).ConfigureAwait(false);

        if (!envelope.Ok)
            throw new InvalidOperationException(FirstNonEmpty(envelope.Message, "poll failed"));

        return envelope.Data;
    }

    private static async Task HandleCommandAsync(
        HttpClient http,
        XingeRemoteConfig config,
        XingeRemoteMessage message,
        Func<TikTokRemoteCommand, Task<TikTokRemoteCommandResult>> executeAsync,
        Action<string>? log,
        CancellationToken ct)
    {
        TikTokRemoteCommandResult result;
        var commandText = message.Payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? message.Payload.GetRawText()
            : "";

        var command = TikTokRemoteCommandParser.Parse(commandText)
                      ?? TikTokRemoteCommandParser.Parse(message.ContentText);

        if (command is null)
        {
            result = TikTokRemoteCommandResult.Failed("unknown", "XINGE 远程命令解析失败");
        }
        else
        {
            result = await executeAsync(command).ConfigureAwait(false);
        }

        await CompleteAsync(http, config, message.ID, result, ct).ConfigureAwait(false);
        log?.Invoke($"XINGE 远程命令完成：{result.SummaryText}");
    }

    private static async Task CompleteAsync(
        HttpClient http,
        XingeRemoteConfig config,
        long messageID,
        TikTokRemoteCommandResult result,
        CancellationToken ct)
    {
        var status = string.Equals(result.Status, "failed", StringComparison.OrdinalIgnoreCase)
            ? "failed"
            : "success";
        var body = new
        {
            client_id = config.ClientId,
            client_token = config.ClientToken,
            status,
            result = new Dictionary<string, object?>
            {
                ["status"] = result.Status,
                ["command"] = result.Command,
                ["summary_text"] = result.SummaryText,
                ["reply_message_type"] = result.ReplyMessageType,
                ["reply_content"] = result.ReplyContent,
                ["completed_at"] = DateTimeOffset.Now.ToString("O"),
            },
        };

        var envelope = await SendAsync<XingeApiEnvelope<JsonElement>>(
            http,
            config,
            HttpMethod.Post,
            $"/client-api/remote/messages/{messageID}/complete",
            body,
            ct).ConfigureAwait(false);

        if (!envelope.Ok)
            throw new InvalidOperationException(FirstNonEmpty(envelope.Message, "complete failed"));
    }

    private static async Task<T> SendAsync<T>(
        HttpClient http,
        XingeRemoteConfig config,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, config.BaseUrl + path);
        request.Headers.TryAddWithoutValidation("X-Remote-Client-ID", config.ClientId);
        request.Headers.TryAddWithoutValidation("X-Remote-Client-Token", config.ClientToken);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var preview = text.Length > 240 ? text[..240] : text;
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {preview}");
        }

        var parsed = JsonSerializer.Deserialize<T>(text, JsonOptions);
        return parsed ?? throw new InvalidOperationException("empty response");
    }

    private void Notify(string message) => StatusChanged?.Invoke(message);

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "")
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private sealed record XingeRemoteConfig(
        string BaseUrl,
        string ClientId,
        string ClientToken,
        string ClientName,
        TimeSpan PollInterval)
    {
        public bool IsReady =>
            !string.IsNullOrWhiteSpace(BaseUrl) &&
            !string.IsNullOrWhiteSpace(ClientId) &&
            !string.IsNullOrWhiteSpace(ClientToken);

        public static XingeRemoteConfig FromSettings(ClientSettings settings)
        {
            var baseUrl = NormalizeBaseUrl(FirstNonEmpty(settings.XingeServerUrl, settings.AuthServerUrl));
            var intervalSeconds = Math.Clamp(settings.XingePollIntervalSeconds <= 0 ? 3 : settings.XingePollIntervalSeconds, 1, 60);
            return new XingeRemoteConfig(
                baseUrl,
                settings.XingeClientId?.Trim() ?? "",
                settings.XingeClientToken?.Trim() ?? "",
                FirstNonEmpty(settings.XingeClientName, "TikTokPublisher"),
                TimeSpan.FromSeconds(intervalSeconds));
        }

        private static string NormalizeBaseUrl(string? value)
        {
            var text = (value ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(text))
                return "";
            if (!Uri.TryCreate(text, UriKind.Absolute, out _))
                text = "http://" + text;
            return text.TrimEnd('/');
        }
    }

    private sealed class XingeApiEnvelope<T>
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class XingeRemoteMessage
    {
        [JsonPropertyName("id")]
        public long ID { get; set; }

        [JsonPropertyName("message_type")]
        public string MessageType { get; set; } = "";

        [JsonPropertyName("content_text")]
        public string ContentText { get; set; } = "";

        [JsonPropertyName("payload")]
        public JsonElement Payload { get; set; }
    }
}
