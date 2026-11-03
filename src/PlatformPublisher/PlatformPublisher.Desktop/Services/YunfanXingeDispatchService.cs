using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.ViewModels;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Services;

public sealed class YunfanXingeDispatchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MainWindowViewModel _viewModel;
    private readonly CancellationTokenSource _cts = new();
    private string _status = "";

    public event Action<string>? StatusChanged;

    public YunfanXingeDispatchService(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    public void Start()
    {
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts.Cancel();

    private void Notify(string message)
    {
        if (string.Equals(_status, message, StringComparison.Ordinal))
            return;
        _status = message;
        StatusChanged?.Invoke(message);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        var handling = false;
        var nextRegister = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
            var config = RemoteConfig.From(settings);
            var delay = config.PollInterval;
            try
            {
                if (!settings.XingeRemoteEnabled || !config.IsReady)
                {
                    delay = TimeSpan.FromSeconds(15);
                    Notify(!settings.XingeRemoteEnabled
                        ? "XINGE 未启用"
                        : "XINGE 未配置用户名/密码、客户端 ID/Token 或服务地址");
                }
                else
                {
                    if (config.NeedsProvisioning)
                    {
                        Notify("XINGE 正在登录账号...");
                        await ProvisionAsync(config, ct).ConfigureAwait(false);
                        Notify("XINGE 账号已登录");
                    }
                    if (!handling && DateTimeOffset.UtcNow >= nextRegister)
                    {
                        if (!_status.StartsWith("XINGE 已在线", StringComparison.Ordinal))
                            Notify("XINGE 正在连接...");
                        await _viewModel.Initialization.ConfigureAwait(false);
                        await RegisterAsync(http, config, ct).ConfigureAwait(false);
                        await RecoverAsync(http, config, ct).ConfigureAwait(false);
                        Notify($"XINGE 已在线：{_viewModel.KsSettleRegistrationAccounts().Count} 个账号");
                        nextRegister = DateTimeOffset.UtcNow.AddSeconds(30);
                    }
                    if (!handling)
                    {
                        var message = await PollAsync(http, config, ct).ConfigureAwait(false);
                        if (message is not null && string.Equals(message.MessageType, "command", StringComparison.OrdinalIgnoreCase))
                        {
                            handling = true;
                            try
                            {
                                await HandleAsync(http, config, message, ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                handling = false;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                delay = TimeSpan.FromSeconds(10);
                Notify($"XINGE 快手分账连接失败：{ex.Message}");
                _viewModel.LogKsSettleDispatch($"XINGE 快手分账连接失败：{ex.Message}");
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleAsync(HttpClient http, RemoteConfig config, RemoteMessage message, CancellationToken ct)
    {
        var payload = message.Payload.ValueKind == JsonValueKind.Object
            ? message.Payload
            : default;
        var commandName = ReadString(payload, "command");
        if (commandName is not ("ks_settlement_upload_series" or "ks_personal_upload_series"))
        {
            await CompleteAsync(http, config, message.Id, "failed", "云帆只执行快手分账命令", "", ct).ConfigureAwait(false);
            return;
        }

        var edition = commandName == "ks_personal_upload_series"
            ? "personal"
            : ReadString(payload, "edition");
        var titles = ReadTitles(payload);
        var command = new MainWindowViewModel.KsSettleCommand(
            commandName,
            ReadLong(payload, "auto_publish_assignment_id"),
            ReadString(payload, "auto_publish_progress_path"),
            ReadString(payload, "account_profile_id"),
            edition,
            titles.FirstOrDefault() ?? "",
            ReadSteps(payload));
        var result = await _viewModel.ExecuteKsSettlementCommandAsync(
            command,
            (status, step, detail, failure) => ReportAsync(http, config, command, status, step, detail, failure, ct),
            ct).ConfigureAwait(false);
        await CompleteAsync(http, config, message.Id, result.Status, result.Summary, result.FailureKind, ct).ConfigureAwait(false);
        if (result.FailureKind == "queue_busy" || (result.Summary ?? "").Contains("当前已有快手分账队列在执行", StringComparison.Ordinal))
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    private async Task RecoverAsync(HttpClient http, RemoteConfig config, CancellationToken ct)
    {
        var envelope = await SendAsync<Envelope<OpenList>>(http, config, HttpMethod.Get, "/client-api/kuaishou/settlement/auto-publish/open", null, ct)
            .ConfigureAwait(false);
        if (!envelope.Ok)
            return;
        var items = (envelope.Data?.Items ?? [])
            .Select(item => new MainWindowViewModel.KsSettleOpenAssignment(
                item.Id, item.Status, item.SeriesId, item.Title, item.AccountProfileId, item.Edition))
            .ToArray();
        await _viewModel.RecoverKsSettleAssignmentsAsync(
            items,
            (id, status, step, detail) => ReportAsync(http, config, id, status, step, detail, status == "interrupted" ? "interrupted" : "", ct),
            ct).ConfigureAwait(false);
    }

    private async Task RegisterAsync(HttpClient http, RemoteConfig config, CancellationToken ct)
    {
        var version = typeof(YunfanXingeDispatchService).Assembly.GetName().Version?.ToString() ?? "unknown";
        var machineId = MachineFingerprintHelper.GetMachineFingerprint();
        if (string.IsNullOrWhiteSpace(machineId))
            machineId = Environment.MachineName;
        var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        var accounts = _viewModel.KsSettleRegistrationAccounts();
        var body = new
        {
            client_id = config.ClientId,
            client_token = config.ClientToken,
            machine_id = machineId,
            device_name = $"云帆-{Environment.MachineName}",
            app_version = $"Yunfan/{version}",
            workspace_path = accounts.FirstOrDefault()?.Workspace ?? "",
            active_account_profile_id = accounts.FirstOrDefault()?.Id ?? "",
            drama_source = settings.DramaSourceChain ?? "",
            account_profiles = accounts.Select(account => new
            {
                id = account.Id,
                name = account.Name,
                auth_ready = account.AuthReady,
                is_current = false,
                remote_dispatch_enabled = account.RemoteDispatchEnabled,
                kuaishou_edition = account.Edition,
                kuaishou_workspace = account.Workspace,
                kuaishou_personal_ready = account.Edition == "personal",
                kuaishou_personal_auth_ready = account.Edition == "personal" && account.AuthReady,
                kuaishou_personal_workspace = account.Edition == "personal" ? account.Workspace : "",
            }).ToArray(),
        };
        var envelope = await SendAsync<Envelope<JsonElement>>(http, config, HttpMethod.Post, "/client-api/remote/register", body, ct)
            .ConfigureAwait(false);
        if (!envelope.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(envelope.Message) ? "register failed" : envelope.Message);
    }

    private static async Task<RemoteMessage?> PollAsync(HttpClient http, RemoteConfig config, CancellationToken ct)
    {
        var envelope = await SendAsync<Envelope<RemoteMessage>>(http, config, HttpMethod.Get, "/client-api/remote/poll", null, ct)
            .ConfigureAwait(false);
        if (!envelope.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(envelope.Message) ? "poll failed" : envelope.Message);
        return envelope.Data is { Id: > 0 } message ? message : null;
    }

    private async Task ReportAsync(
        HttpClient http,
        RemoteConfig config,
        MainWindowViewModel.KsSettleCommand command,
        string status,
        string step,
        string detail,
        string failureKind,
        CancellationToken ct)
    {
        if (command.AssignmentId <= 0)
            return;
        await ReportAsync(http, config, command.AssignmentId, status, step, detail, failureKind, ct).ConfigureAwait(false);
    }

    private async Task ReportAsync(
        HttpClient http,
        RemoteConfig config,
        long assignmentId,
        string status,
        string step,
        string detail,
        string failureKind,
        CancellationToken ct)
    {
        if (assignmentId <= 0)
            return;
        var path = $"/client-api/kuaishou/settlement/auto-publish/assignments/{assignmentId}/progress";
        var body = new
        {
            client_id = config.ClientId,
            client_token = config.ClientToken,
            status,
            step,
            detail,
            message = detail,
            failure_kind = failureKind,
        };
        try
        {
            await SendAsync<Envelope<JsonElement>>(http, config, HttpMethod.Post, path, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _viewModel.LogKsSettleDispatch($"XINGE 快手分账进度上报失败：{ex.Message}");
        }
    }

    private static async Task CompleteAsync(
        HttpClient http,
        RemoteConfig config,
        long messageId,
        string status,
        string summary,
        string failureKind,
        CancellationToken ct)
    {
        var normalized = (status ?? "").Trim().ToLowerInvariant() switch
        {
            "success" => "success",
            "accepted" => "accepted",
            _ => "failed",
        };
        var body = new
        {
            client_id = config.ClientId,
            client_token = config.ClientToken,
            status = normalized,
            result = new Dictionary<string, object?>
            {
                ["status"] = normalized,
                ["summary_text"] = summary,
                ["failure_kind"] = failureKind,
                ["completed_at"] = DateTimeOffset.Now.ToString("O"),
            },
        };
        var envelope = await SendAsync<Envelope<JsonElement>>(
            http, config, HttpMethod.Post, $"/client-api/remote/messages/{messageId}/complete", body, ct).ConfigureAwait(false);
        if (!envelope.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(envelope.Message) ? "complete failed" : envelope.Message);
    }

    private static async Task ProvisionAsync(RemoteConfig config, CancellationToken ct)
    {
        var credentials = await XingeRemoteAccountService.ProvisionAsync(config.Settings, ct).ConfigureAwait(false);
        var persisted = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        persisted.XingeClientId = credentials.ClientId;
        persisted.XingeClientToken = credentials.ClientToken;
        persisted.XingeCredentialFingerprint = credentials.CredentialFingerprint;
        if (string.IsNullOrWhiteSpace(persisted.XingeClientName) ||
            persisted.XingeClientName.Contains("tiktok", StringComparison.OrdinalIgnoreCase))
            persisted.XingeClientName = "云帆";
        ClientSettingsStore.Save(persisted, PlatformPublisherPaths.SettingsDatabasePath);
        config.ClientId = credentials.ClientId;
        config.ClientToken = credentials.ClientToken;
    }

    private static async Task<T> SendAsync<T>(
        HttpClient http,
        RemoteConfig config,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, config.BaseUrl + path);
        request.Headers.TryAddWithoutValidation("X-Remote-Client-ID", config.ClientId);
        request.Headers.TryAddWithoutValidation("X-Remote-Client-Token", config.ClientToken);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Trim(text)}");
        return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new InvalidOperationException("empty response");
    }

    private static string ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
            : "";

    private static long ReadLong(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return long.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static IReadOnlyList<string> ReadTitles(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("titles", out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadSteps(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("enabled_steps", out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return value.EnumerateArray()
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static string Trim(string text) =>
        text.Length <= 180 ? text : text[..180];

    private sealed class RemoteConfig
    {
        public ClientSettings Settings { get; private set; } = new();
        public string BaseUrl { get; private set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientToken { get; set; } = "";
        public TimeSpan PollInterval { get; private set; } = TimeSpan.FromSeconds(3);
        public bool NeedsProvisioning => XingeRemoteAccountService.NeedsProvisioning(Settings);
        public bool IsReady => BaseUrl.Length > 0 && (ClientId.Length > 0 && ClientToken.Length > 0 || XingeRemoteAccountService.HasPasswordCredentials(Settings));

        public static RemoteConfig From(ClientSettings settings)
        {
            var raw = string.IsNullOrWhiteSpace(settings.XingeServerUrl) ? settings.AuthServerUrl : settings.XingeServerUrl;
            var text = (raw ?? "").Trim().TrimEnd('/');
            if (text.Length > 0 && !text.Contains("://", StringComparison.Ordinal))
                text = "http://" + text;
            var seconds = Math.Clamp(settings.XingePollIntervalSeconds <= 0 ? 3 : settings.XingePollIntervalSeconds, 1, 60);
            return new RemoteConfig
            {
                Settings = settings,
                BaseUrl = text,
                ClientId = settings.XingeClientId?.Trim() ?? "",
                ClientToken = settings.XingeClientToken?.Trim() ?? "",
                PollInterval = TimeSpan.FromSeconds(seconds),
            };
        }
    }

    private sealed class Envelope<T>
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class RemoteMessage
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
        [JsonPropertyName("message_type")]
        public string MessageType { get; set; } = "";
        [JsonPropertyName("payload")]
        public JsonElement Payload { get; set; }
    }

    private sealed class OpenList
    {
        [JsonPropertyName("items")]
        public List<OpenItem> Items { get; set; } = [];
    }

    private sealed class OpenItem
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
        [JsonPropertyName("series_id")]
        public string SeriesId { get; set; } = "";
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
        [JsonPropertyName("account_profile_id")]
        public string AccountProfileId { get; set; } = "";
        [JsonPropertyName("edition")]
        public string Edition { get; set; } = "";
    }
}
