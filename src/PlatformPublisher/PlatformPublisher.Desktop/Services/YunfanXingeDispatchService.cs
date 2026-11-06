using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;

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
    private readonly object _quotaFullLock = new();
    private readonly DispatchConfirmCoordinator _confirm = new();
    private readonly Dictionary<long, RemoteMessage> _heldConfirmMessages = new();
    private Dictionary<string, DateTimeOffset> _quotaFullUntil = new(StringComparer.OrdinalIgnoreCase);
    private string _dispatchPauseDate = "";
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
        RemoteMessage? replay = null;
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
                        if (await ConsumeConfirmAsync(http, config, ct).ConfigureAwait(false))
                            continue;
                        var message = replay ?? await PollAsync(http, config, ct).ConfigureAwait(false);
                        replay = null;
                        if (message is not null && string.Equals(message.MessageType, "command", StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryReadConfirmItem(message, out var item))
                            {
                                if (IsAccountQuotaFull(ReadString(message.Payload, "account_profile_id")))
                                {
                                    var title = item.Title;
                                    _viewModel.LogKsSettleDispatch($"账号今日上传已达上限，跳过快手分账自动派单：{title}");
                                    await CompleteAsync(http, config, message.Id, "success", "账号今日上传已达上限，已跳过", "", ct).ConfigureAwait(false);
                                }
                                else if (IsDispatchPaused)
                                {
                                    await PauseDayAsync(http, config, ct).ConfigureAwait(false);
                                }
                                else
                                {
                                    _heldConfirmMessages[message.Id] = message;
                                    _confirm.ExpireIfDue(DateTimeOffset.Now);
                                    var offer = _confirm.Offer(item, DateTimeOffset.Now, out var session);
                                    if (offer == DispatchConfirmOffer.PendingDecision)
                                    {
                                        _heldConfirmMessages.Remove(message.Id);
                                        await ConsumeConfirmAsync(http, config, ct).ConfigureAwait(false);
                                        replay = message;
                                    }
                                    else if (offer == DispatchConfirmOffer.Opened && session is not null)
                                    {
                                        XingeDispatchConfirmPrompt.Show(session);
                                    }
                                }
                            }
                            else
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
                            continue;
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
                _confirm.ExpireIfDue(DateTimeOffset.Now);
                var decision = _confirm.DecisionTask;
                if (decision is { IsCompleted: false })
                    await Task.WhenAny(Task.Delay(delay, ct), decision).ConfigureAwait(false);
                else if (decision is null)
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
        var accountId = ReadString(payload, "account_profile_id");
        var assignmentId = ReadLong(payload, "auto_publish_assignment_id");
        if (assignmentId > 0 && IsAccountQuotaFull(accountId))
        {
            var title = ReadTitles(payload).FirstOrDefault() ?? "";
            _viewModel.LogKsSettleDispatch($"账号今日上传已达上限，跳过快手分账自动派单：{title}");
            await CompleteAsync(http, config, message.Id, "success", "账号今日上传已达上限，已跳过", "", ct).ConfigureAwait(false);
            return;
        }
        var titles = ReadTitles(payload);
        var command = new MainWindowViewModel.KsSettleCommand(
            commandName,
            assignmentId,
            ReadString(payload, "auto_publish_progress_path"),
            accountId,
            edition,
            titles.FirstOrDefault() ?? "",
            ReadSteps(payload),
            ReadQueueBool(payload, "force_rerun_completed_steps"));
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
            .Where(item => !IsAccountQuotaFull(item.AccountProfileId))
            .Select(item => new MainWindowViewModel.KsSettleOpenAssignment(
                item.Id, item.Status, item.SeriesId, item.Title, item.AccountProfileId, item.Edition))
            .ToArray();
        var skipped = (envelope.Data?.Items.Count ?? 0) - items.Length;
        if (skipped > 0)
            _viewModel.LogKsSettleDispatch($"账号今日上传已达上限，已跳过 {skipped} 个快手分账自动派单恢复。");
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
            account_profiles = accounts.Select(account =>
            {
                var profile = new Dictionary<string, object?>
                {
                    ["id"] = account.Id,
                    ["name"] = account.Name,
                    ["auth_ready"] = account.AuthReady,
                    ["is_current"] = false,
                    ["remote_dispatch_enabled"] = account.RemoteDispatchEnabled,
                    ["queue_running"] = account.QueueRunning,
                    ["kuaishou_edition"] = account.Edition,
                    ["kuaishou_workspace"] = account.Workspace,
                    ["kuaishou_personal_ready"] = account.Edition == "personal",
                    ["kuaishou_personal_auth_ready"] = account.Edition == "personal" && account.AuthReady,
                    ["kuaishou_personal_workspace"] = account.Edition == "personal" ? account.Workspace : "",
                };
                if (!IsAccountQuotaFull(account.Id) && TryListTodayUploads(account, out var uploads))
                {
                    profile["uploaded_today"] = uploads.Select(item => new
                    {
                        series_id = "",
                        title = item.Title,
                        completed_at = item.CompletedAt,
                    }).ToArray();
                }
                return profile;
            }).ToArray(),
        };
        var envelope = await SendAsync<Envelope<JsonElement>>(http, config, HttpMethod.Post, "/client-api/remote/register", body, ct)
            .ConfigureAwait(false);
        if (!envelope.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(envelope.Message) ? "register failed" : envelope.Message);
        RememberQuotaFullAccounts(envelope.QuotaFullAccounts);
        RememberDispatchPause(envelope);
    }

    private static bool TryListTodayUploads(
        MainWindowViewModel.KsSettleRegistrationAccount account,
        out IReadOnlyList<KuaishouTodayUploadCounter.TodayUpload> uploads)
    {
        uploads = [];
        try
        {
            var platform = string.Equals(account.Edition, "enterprise", StringComparison.OrdinalIgnoreCase)
                ? PublishPlatform.KuaishouEnterpriseRevenue
                : PublishPlatform.KuaishouPersonalRevenue;
            uploads = KuaishouTodayUploadCounter.ListToday(platform, account.Id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsAccountQuotaFull(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return false;
        var now = DateTimeOffset.UtcNow;
        lock (_quotaFullLock)
        {
            return _quotaFullUntil.TryGetValue(accountId.Trim(), out var until) && now < until;
        }
    }

    private void RememberQuotaFullAccounts(List<QuotaFullAccount>? accounts)
    {
        if (accounts is null)
            return;
        var next = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            var id = account.Id?.Trim() ?? "";
            if (id.Length == 0)
                continue;
            if (!DateTimeOffset.TryParse(account.Until, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until))
                continue;
            next[id] = until;
        }

        lock (_quotaFullLock)
            _quotaFullUntil = next;
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

    private bool IsDispatchPaused => _dispatchPauseDate.Length > 0;

    private void RememberDispatchPause(Envelope<JsonElement> envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.DispatchPauseDate) && string.IsNullOrWhiteSpace(envelope.BusinessDate))
            return;
        var date = string.IsNullOrWhiteSpace(envelope.BusinessDate) ? envelope.DispatchPauseDate : envelope.BusinessDate;
        _dispatchPauseDate = envelope.DispatchPaused ? date : "";
    }

    private bool TryReadConfirmItem(RemoteMessage message, out DispatchConfirmItem item)
    {
        item = new DispatchConfirmItem();
        var payload = message.Payload.ValueKind == JsonValueKind.Object ? message.Payload : default;
        var commandName = ReadString(payload, "command");
        if (commandName is not ("ks_settlement_upload_series" or "ks_personal_upload_series"))
            return false;
        var assignmentId = ReadLong(payload, "auto_publish_assignment_id");
        if (assignmentId <= 0)
            return false;
        item = new DispatchConfirmItem
        {
            MessageId = message.Id,
            AssignmentId = assignmentId,
            AccountName = ReadString(payload, "account_profile_name"),
            Edition = commandName == "ks_personal_upload_series" ? "personal" : ReadString(payload, "edition"),
            Title = ReadTitles(payload).FirstOrDefault() ?? "",
            ConfirmSeconds = (int)ReadLong(payload, "confirm_seconds"),
        };
        return true;
    }

    private async Task<bool> ConsumeConfirmAsync(HttpClient http, RemoteConfig config, CancellationToken ct)
    {
        if (!_confirm.TryConsume(out var decision, out var items))
            return false;
        switch (decision)
        {
            case DispatchConfirmDecision.RejectOnce:
                await DeclineOnceAsync(http, config, items, ct).ConfigureAwait(false);
                foreach (var item in items)
                    _heldConfirmMessages.Remove(item.MessageId);
                break;
            case DispatchConfirmDecision.RejectToday:
                await PauseDayAsync(http, config, ct).ConfigureAwait(false);
                _heldConfirmMessages.Clear();
                break;
            default:
                foreach (var item in items)
                {
                    if (!_heldConfirmMessages.Remove(item.MessageId, out var message))
                        continue;
                    await HandleAsync(http, config, message, ct).ConfigureAwait(false);
                }
                break;
        }
        return true;
    }

    private async Task DeclineOnceAsync(HttpClient http, RemoteConfig config, IReadOnlyList<DispatchConfirmItem> items, CancellationToken ct)
    {
        var ids = items.Select(item => item.AssignmentId).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return;
        try
        {
            await SendAsync<Envelope<JsonElement>>(http, config, HttpMethod.Post,
                "/client-api/kuaishou/settlement/auto-publish/decline",
                new { client_id = config.ClientId, client_token = config.ClientToken, assignment_ids = ids }, ct).ConfigureAwait(false);
            _viewModel.LogKsSettleDispatch($"已拒绝本次快手分账派单 {ids.Length} 部，下次仍会提示。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _viewModel.LogKsSettleDispatch($"拒绝本次快手分账派单失败：{ex.Message}");
        }
    }

    private async Task PauseDayAsync(HttpClient http, RemoteConfig config, CancellationToken ct)
    {
        try
        {
            var envelope = await SendAsync<Envelope<JsonElement>>(http, config, HttpMethod.Post,
                "/client-api/kuaishou/settlement/auto-publish/pause-day",
                new { client_id = config.ClientId, client_token = config.ClientToken }, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(envelope.BusinessDate))
                _dispatchPauseDate = envelope.BusinessDate;
            else if (!string.IsNullOrWhiteSpace(envelope.DispatchPauseDate))
                _dispatchPauseDate = envelope.DispatchPauseDate;
            else
                _dispatchPauseDate = "paused";
            _viewModel.LogKsSettleDispatch("今日不再接收快手分账自动派单，尚未开始的剧已释放。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _viewModel.LogKsSettleDispatch($"今日不再接收快手分账派单失败：{ex.Message}");
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

    private static bool ReadQueueBool(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("queue_options", out var options) || options.ValueKind != JsonValueKind.Object)
            return false;
        if (!options.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase) || value.GetString() == "1",
            _ => false,
        };
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

        [JsonPropertyName("quota_full_accounts")]
        public List<QuotaFullAccount>? QuotaFullAccounts { get; set; }

        [JsonPropertyName("dispatch_paused")]
        public bool DispatchPaused { get; set; }

        [JsonPropertyName("dispatch_pause_date")]
        public string DispatchPauseDate { get; set; } = "";

        [JsonPropertyName("business_date")]
        public string BusinessDate { get; set; } = "";
    }

    private sealed class QuotaFullAccount
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("edition")]
        public string Edition { get; set; } = "";

        [JsonPropertyName("until")]
        public string Until { get; set; } = "";
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
