using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Ui.Services;

public sealed record XingeRemoteRegistrationSnapshot(
    string WorkspacePath,
    string ActiveAccountProfileId,
    string DramaSource,
    IReadOnlyList<XingeRemoteAccountSnapshot> AccountProfiles,
    IReadOnlyList<XingeTikTokProcessedFact>? ProcessedSeries = null,
    bool DispatchSyncing = false);

public sealed record XingeRemoteUploadedDrama(string SeriesId, string Title, string CompletedAt);

public sealed record XingeRemoteAccountSnapshot(
    string Id,
    string Name,
    bool AuthReady,
    bool IsCurrent,
    bool RemoteDispatchEnabled,
    IReadOnlyList<XingeRemoteUploadedDrama>? UploadedToday = null,
    bool QueueRunning = false,
    string DramaMarket = "",
    string DramaSource = "");

public sealed class XingeRemoteCommandService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static readonly TimeSpan RegistrationInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private readonly object _sync = new();
    private readonly object _quotaFullLock = new();
    private Dictionary<string, DateTimeOffset> _quotaFullUntil = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public event Action<string>? StatusChanged;

    // 这部剧的本地项目还在时，轮询到的命令直接续跑，不再弹出新派单确认。
    public Func<long, bool>? AssignmentAlreadyLocal { get; set; }

    public Func<IReadOnlyList<long>>? LocalStoppedAssignmentIds { get; set; }

    public void Restart(
        Func<TikTokRemoteCommand, Task<TikTokRemoteCommandResult>> executeAsync,
        Func<CancellationToken, Task<XingeRemoteRegistrationSnapshot>> presenceProvider,
        Func<bool, CancellationToken, Task<XingeRemoteRegistrationSnapshot>> snapshotProvider,
        Action<string>? log = null,
        Func<IReadOnlyList<XingeOpenAutoPublishAssignment>, Task>? recoverAsync = null,
        Action? onIdle = null)
    {
        Stop();

        var settings = ClientSettingsStore.Load();
        var mode = XingeRuntimeConnection.Mode(settings);
        var signedIn = LicenseStore.Load().IsActivated();
        XingeRuntimeConnection.SetManualAvailable(signedIn && mode == XingeRuntimeConnection.Manual);
        if (mode != XingeRuntimeConnection.FollowLogin)
            XingeRuntimeConnection.ClearAutomatic();
        if (mode == XingeRuntimeConnection.Off)
        {
            Notify("XINGE 未启用");
            return;
        }

        if (!signedIn)
        {
            XingeRuntimeConnection.ClearAutomatic();
            Notify("请先完成软件登录，XINGE 监听尚未启动");
            return;
        }

        var config = XingeRemoteConfig.FromSettings(settings);
        if (!config.IsReady)
        {
            Notify(mode == XingeRuntimeConnection.FollowLogin
                ? "请先完成软件登录，XINGE 将自动连接"
                : "请填写 XINGE 手动连接的地址和账户凭据");
            return;
        }

        Notify(config.NeedsProvisioning ? "XINGE 正在注册本机..." : "XINGE 正在连接...");
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _cts = cts;
            _worker = Task.Run(() => RunAsync(config, executeAsync, presenceProvider, snapshotProvider, log, recoverAsync, onIdle, cts.Token));
        }
    }

    public void ReportDailySeriesLimit(string? accountProfileId)
    {
        var accountId = accountProfileId?.Trim() ?? "";
        if (accountId.Length == 0)
            return;
        _ = Task.Run(() => ReportDailySeriesLimitAsync(accountId));
    }

    private async Task ReportDailySeriesLimitAsync(string accountId)
    {
        try
        {
            var settings = ClientSettingsStore.Load();
            var config = XingeRemoteConfig.FromSettings(settings);
            if (!config.IsReady || (config.SoftwareSession is not null && !config.HasClientCredentials))
                return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            if (config.NeedsProvisioning)
                await ProvisionAndSaveAsync(config, CancellationToken.None).ConfigureAwait(false);
            await SendAsync<XingeApiEnvelope<JsonElement>>(
                http,
                config,
                HttpMethod.Post,
                "/client-api/tiktok/auto-publish/daily-limit",
                new
                {
                    client_id = config.ClientId,
                    client_token = config.ClientToken,
                    account_profile_id = accountId,
                    detail = "TikTok 今日创建剧集已达上限",
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"通知 XINGE 停止当天分发失败：{ex.Message}");
        }
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
        lock (_quotaFullLock)
            _quotaFullUntil.Clear();
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
        Func<CancellationToken, Task<XingeRemoteRegistrationSnapshot>> presenceProvider,
        Func<bool, CancellationToken, Task<XingeRemoteRegistrationSnapshot>> snapshotProvider,
        Action<string>? log,
        Func<IReadOnlyList<XingeOpenAutoPublishAssignment>, Task>? recoverAsync,
        Action? onIdle,
        CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        var lastRegisteredAt = DateTimeOffset.MinValue;
        var startupReady = false;
        var sentProcessedSeries = new HashSet<XingeTikTokProcessedFact>();
        XingeRemoteMessage? replay = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (config.SoftwareSession is { } softwareSession && XingeRuntimeConnection.ReadSoftwareSession()?.SessionKey != softwareSession.SessionKey)
                {
                    Notify("软件登录已变化，XINGE 监听已停止");
                    return;
                }
                if (config.NeedsProvisioning)
                    await ProvisionAndSaveAsync(config, ct).ConfigureAwait(false);

                if (DateTimeOffset.UtcNow - lastRegisteredAt >= RegistrationInterval)
                {
                    XingeRemoteRegistrationSnapshot snapshot;
                    if (!startupReady)
                    {
                        var presence = await presenceProvider(ct).ConfigureAwait(false);
                        snapshot = await RegisterStartupAsync(presence, async token =>
                        {
                            var full = await snapshotProvider(true, token).ConfigureAwait(false);
                            if (!await SyncProcessedSeriesAsync(http, config, full.ProcessedSeries, sentProcessedSeries, log, token).ConfigureAwait(false))
                                throw new InvalidOperationException("历史同步暂未完成，稍后重试");
                            return full;
                        }, async (pending, token) =>
                        {
                            await RegisterAsync(http, config, pending, presenceProvider, token).ConfigureAwait(false);
                            Notify($"XINGE 已在线：{pending.AccountProfiles.Count} 个账号，正在同步派单数据...");
                        }, RegistrationInterval, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        snapshot = await snapshotProvider(false, ct).ConfigureAwait(false);
                        await SyncProcessedSeriesAsync(http, config, snapshot.ProcessedSeries, sentProcessedSeries, log, ct).ConfigureAwait(false);
                    }

                    ct.ThrowIfCancellationRequested();
                    var quotaFull = await RegisterAsync(http, config, snapshot, presenceProvider, ct).ConfigureAwait(false);
                    startupReady = true;
                    ct.ThrowIfCancellationRequested();
                    RememberQuotaFullAccounts(quotaFull);
                    lastRegisteredAt = DateTimeOffset.UtcNow;
                    Notify($"XINGE 已在线：{snapshot.AccountProfiles.Count} 个账号");
                    await FlushAutoPublishProgressAsync(log, ct).ConfigureAwait(false);
                    if (recoverAsync is not null)
                    {
                        if (!config.StoppedReclaimDone)
                        {
                            var stoppedIds = LocalStoppedAssignmentIds?.Invoke();
                            config.StoppedReclaimDone = stoppedIds is not { Count: > 0 }
                                || await ResumeStoppedAsync(http, config, stoppedIds, log, ct).ConfigureAwait(false);
                        }
                        if (config.StoppedReclaimDone)
                        {
                            var open = await ListOpenAutoPublishAsync(http, config, ct).ConfigureAwait(false);
                            await recoverAsync(open).ConfigureAwait(false);
                        }
                    }
                }

                if (await ConsumeConfirmAsync(http, config, executeAsync, log, ct).ConfigureAwait(false))
                    continue;

                var message = replay ?? await PollAsync(http, config, ct).ConfigureAwait(false);
                replay = null;
                if (message is not null)
                {
                    if (TryReadConfirmItem(message, out var item, out var accountId))
                    {
                        if (AssignmentAlreadyLocal?.Invoke(item.AssignmentId) == true)
                        {
                            log?.Invoke($"派单 {item.Title} 的本地项目还在，直接续跑，不再当作新派单。");
                            await HandleCommandAsync(http, config, message, executeAsync, log, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (IsAccountQuotaFull(accountId))
                        {
                            log?.Invoke($"账号今日上传已达上限，跳过 TikTok 自动派单：{item.Title}");
                            await CompleteAsync(http, config, message.ID, TikTokRemoteCommandResult.Success("tiktok_upload_series", "账号今日上传已达上限，已跳过"), ct).ConfigureAwait(false);
                        }
                        else if (config.DispatchPauseDate.Length > 0)
                        {
                            // 服务端可以手动恢复。这里不再次上报暂停，等下一次注册读到未暂停后再收这条剧。
                            replay = message;
                            log?.Invoke("今日不再接收仍在生效，这条派单先留着，等服务端恢复后再接收。");
                            await Task.Delay(config.PollInterval, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            config.HeldConfirmMessages[message.ID] = message;
                            config.Confirm.ExpireIfDue(DateTimeOffset.Now);
                            var offer = config.Confirm.Offer(item, DateTimeOffset.Now, out var session);
                            if (offer == DispatchConfirmOffer.PendingDecision)
                            {
                                config.HeldConfirmMessages.Remove(message.ID);
                                await ConsumeConfirmAsync(http, config, executeAsync, log, ct).ConfigureAwait(false);
                                replay = message;
                            }
                            else if (offer == DispatchConfirmOffer.Opened && session is not null)
                            {
                                var dismissOnStop = ct.Register(() => session.Decision.TrySetResult(DispatchConfirmDecision.Dismissed));
                                _ = session.Decision.Task.ContinueWith(_ => dismissOnStop.Dispose(), TaskScheduler.Default);
                                XingeDispatchConfirmPrompt.Show(session);
                            }
                        }
                    }
                    else
                    {
                        await HandleCommandAsync(http, config, message, executeAsync, log, ct).ConfigureAwait(false);
                    }
                    continue;
                }

                try
                {
                    onIdle?.Invoke();
                }
                catch (Exception ex)
                {
                    log?.Invoke($"自动派单调度失败：{ex.Message}");
                }

                config.Confirm.ExpireIfDue(DateTimeOffset.Now);
                var decision = config.Confirm.DecisionTask;
                if (decision is { IsCompleted: false })
                    await Task.WhenAny(Task.Delay(config.PollInterval, ct), decision).ConfigureAwait(false);
                else if (decision is null)
                    await Task.Delay(config.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (XingeRemoteAuthenticationException) when (config.CanProvision)
            {
                try
                {
                    await ProvisionAndSaveAsync(config, ct).ConfigureAwait(false);
                    lastRegisteredAt = DateTimeOffset.MinValue;
                    startupReady = false;
                    sentProcessedSeries.Clear();
                }
                catch (Exception ex)
                {
                    var text = $"XINGE 重新登录失败：{ex.Message}";
                    Notify(text);
                    log?.Invoke(text);
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                }
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

    internal static async Task<XingeRemoteRegistrationSnapshot> RegisterStartupAsync(
        XingeRemoteRegistrationSnapshot presence,
        Func<CancellationToken, Task<XingeRemoteRegistrationSnapshot>> loadFullSnapshot,
        Func<XingeRemoteRegistrationSnapshot, CancellationToken, Task> registerPresence,
        TimeSpan heartbeatInterval,
        CancellationToken ct)
    {
        var pending = presence with
        {
            AccountProfiles = presence.AccountProfiles.Select(account => account with
            {
                UploadedToday = null,
            }).ToArray(),
            ProcessedSeries = null,
            DispatchSyncing = true,
        };
        await registerPresence(pending, ct).ConfigureAwait(false);

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var full = loadFullSnapshot(startup.Token);
        try
        {
            while (true)
            {
                try
                {
                    return await full.WaitAsync(heartbeatInterval, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    if (full.IsCompleted)
                        return await full.ConfigureAwait(false);
                    await registerPresence(pending, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            startup.Cancel();
            await ((Task)full).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public bool IsAccountQuotaFull(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return false;
        var now = DateTimeOffset.UtcNow;
        lock (_quotaFullLock)
        {
            return _quotaFullUntil.TryGetValue(accountId.Trim(), out var until)
                   && XingeQuotaFull.IsHeld(until, now);
        }
    }

    public HashSet<string> QuotaFullAccountIds()
    {
        var now = DateTimeOffset.UtcNow;
        var full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_quotaFullLock)
        {
            foreach (var pair in _quotaFullUntil)
            {
                if (XingeQuotaFull.IsHeld(pair.Value, now))
                    full.Add(pair.Key);
            }
        }

        return full;
    }

    private void RememberQuotaFullAccounts(List<XingeQuotaFullAccount>? accounts)
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

    private async Task<List<XingeQuotaFullAccount>?> RegisterAsync(
        HttpClient http,
        XingeRemoteConfig config,
        XingeRemoteRegistrationSnapshot snapshot,
        Func<CancellationToken, Task<XingeRemoteRegistrationSnapshot>> presenceProvider,
        CancellationToken ct)
    {
        await _registrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var present = await presenceProvider(ct).ConfigureAwait(false);
            var latest = present.AccountProfiles.ToDictionary(account => account.Id, StringComparer.Ordinal);
            var profiles = snapshot.AccountProfiles.Where(account => latest.ContainsKey(account.Id)).Select(account =>
            {
                var current = latest[account.Id];
                return account with { RemoteDispatchEnabled = current.RemoteDispatchEnabled, AuthReady = current.AuthReady };
            }).ToArray();
            var version = typeof(XingeRemoteCommandService).Assembly.GetName().Version?.ToString() ?? "unknown";
            var machineId = config.SoftwareSession?.MachineId ?? MachineFingerprintHelper.GetMachineFingerprint();
            if (string.IsNullOrWhiteSpace(machineId))
                machineId = Environment.MachineName;
            var body = new
            {
                client_id = config.ClientId,
                client_token = config.ClientToken,
                machine_id = machineId,
                device_name = FirstNonEmpty(config.ClientName, $"TikTokPublisher-{Environment.MachineName}"),
                app_version = $"TikTokPublisher/{version}",
                workspace_path = snapshot.WorkspacePath,
                active_account_profile_id = snapshot.ActiveAccountProfileId,
                drama_source = snapshot.DramaSource,
                dramabox_server_media = true,
                project_slot_capacity = ClientSettingsStore.Load().TiktokGlobalProjectConcurrency,
                tiktok_dispatch_syncing = snapshot.DispatchSyncing,
                account_profiles = profiles.Select(account =>
                {
                    var profile = new Dictionary<string, object?>
                    {
                        ["id"] = account.Id,
                        ["name"] = account.Name,
                        ["auth_ready"] = account.AuthReady,
                        ["is_current"] = account.IsCurrent,
                        ["remote_dispatch_enabled"] = !snapshot.DispatchSyncing && account.RemoteDispatchEnabled,
                        ["remote_dispatch_preference"] = account.RemoteDispatchEnabled,
                        ["queue_running"] = account.QueueRunning,
                        ["drama_market"] = TikTokPublisher.Core.Models.TikTokAccountProfile.NormalizeDramaMarket(account.DramaMarket),
                        ["drama_source"] = string.IsNullOrWhiteSpace(account.DramaSource) ? snapshot.DramaSource : account.DramaSource,
                    };
                    if (account.UploadedToday is not null)
                    {
                        profile["uploaded_today"] = account.UploadedToday.Select(item => new
                        {
                            series_id = item.SeriesId,
                            title = item.Title,
                            completed_at = item.CompletedAt,
                        }).ToArray();
                    }
                    return profile;
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
            RememberDispatchPause(config, envelope);
            ClientSettingsStore.RememberServerDispatchMode(envelope.DispatchMode);
            return envelope.QuotaFullAccounts;
            }
        finally
        {
            _registrationGate.Release();
        }
    }

    public async Task SyncAccountDispatchPreferenceAsync(string profileId,
        Func<CancellationToken, Task<XingeRemoteRegistrationSnapshot>> presenceProvider)
    {
        CancellationToken ct;
        lock (_sync)
        {
            if (_cts is null)
                return;
            ct = _cts.Token;
        }

        Notify("账号配置已保存，正在同步至 XINGE...");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await _registrationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var config = XingeRemoteConfig.FromSettings(ClientSettingsStore.Load());
                    if (config.HasClientCredentials)
                    {
                        var snapshot = await presenceProvider(ct).ConfigureAwait(false);
                        var profile = snapshot.AccountProfiles.FirstOrDefault(account => account.Id == profileId);
                        if (profile is null)
                            return;
                        var response = await SendAsync<XingeApiEnvelope<JsonElement>>(http, config, HttpMethod.Post,
                            "/client-api/tiktok/account-dispatch", new
                            {
                                account_profile_id = profile.Id,
                                remote_dispatch_enabled = profile.RemoteDispatchEnabled,
                                auth_ready = profile.AuthReady,
                            }, ct).ConfigureAwait(false);
                        if (response.Ok)
                        {
                            Notify(response.DispatchSyncing
                                ? $"{profile.Name} 的远程分发设置已同步至 XINGE，派单数据仍在同步。"
                                : $"{profile.Name} 的远程分发设置已同步至 XINGE。");
                            return;
                        }
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // The next attempt reads the current preference again, including an intervening save.
                }
                finally
                {
                    _registrationGate.Release();
                }
                if (attempt < 2)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            Notify("账号配置已保存，等待 XINGE 下次注册同步。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
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

    private static void RememberDispatchPause(XingeRemoteConfig config, XingeApiEnvelope<JsonElement> envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.DispatchPauseDate) && string.IsNullOrWhiteSpace(envelope.BusinessDate))
            return;
        var date = string.IsNullOrWhiteSpace(envelope.BusinessDate) ? envelope.DispatchPauseDate : envelope.BusinessDate;
        config.DispatchPauseDate = envelope.DispatchPaused ? date : "";
    }

    private static int ReadConfirmSeconds(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("confirm_seconds", out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static bool TryReadConfirmItem(XingeRemoteMessage message, out DispatchConfirmItem item, out string accountId)
    {
        item = new DispatchConfirmItem();
        accountId = "";
        var commandText = message.Payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? message.Payload.GetRawText()
            : "";
        var command = TikTokRemoteCommandParser.Parse(commandText)
                      ?? TikTokRemoteCommandParser.Parse(message.ContentText);
        if (command is null || !command.IsAutoPublishAssignment)
            return false;
        accountId = command.AccountProfileId ?? "";
        item = new DispatchConfirmItem
        {
            MessageId = message.ID,
            AssignmentId = command.AutoPublishAssignmentId,
            AccountName = command.AccountProfileName ?? "",
            ConfirmSeconds = ReadConfirmSeconds(message.Payload),
            Title = command.Titles?.FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
                    ?? command.Series?.Select(spec => spec.Title).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
                    ?? "",
        };
        return true;
    }

    private async Task<bool> ConsumeConfirmAsync(
        HttpClient http,
        XingeRemoteConfig config,
        Func<TikTokRemoteCommand, Task<TikTokRemoteCommandResult>> executeAsync,
        Action<string>? log,
        CancellationToken ct)
    {
        if (!config.Confirm.TryConsume(out var decision, out var items))
            return false;
        switch (decision)
        {
            case DispatchConfirmDecision.RejectOnce:
                await DeclineOnceAsync(http, config, items, log, ct).ConfigureAwait(false);
                foreach (var item in items)
                    config.HeldConfirmMessages.Remove(item.MessageId);
                break;
            case DispatchConfirmDecision.RejectToday:
                await PauseDayAsync(http, config, log, ct).ConfigureAwait(false);
                config.HeldConfirmMessages.Clear();
                break;
            case DispatchConfirmDecision.Dismissed:
                foreach (var item in items)
                    config.HeldConfirmMessages.Remove(item.MessageId);
                log?.Invoke("已关闭派单确认。这批剧仍保留，重启后会再提示，不会另派新剧。");
                break;
            default:
                foreach (var item in items)
                {
                    if (!config.HeldConfirmMessages.Remove(item.MessageId, out var message))
                        continue;
                    await HandleCommandAsync(http, config, message, executeAsync, log, ct).ConfigureAwait(false);
                }
                break;
        }
        return true;
    }

    private async Task DeclineOnceAsync(
        HttpClient http,
        XingeRemoteConfig config,
        IReadOnlyList<DispatchConfirmItem> items,
        Action<string>? log,
        CancellationToken ct)
    {
        var ids = items.Select(item => item.AssignmentId).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return;
        try
        {
            await SendAsync<XingeApiEnvelope<JsonElement>>(http, config, HttpMethod.Post,
                "/client-api/tiktok/auto-publish/decline",
                new { client_id = config.ClientId, client_token = config.ClientToken, assignment_ids = ids }, ct).ConfigureAwait(false);
            log?.Invoke($"已拒绝本次 TikTok 派单 {ids.Length} 部。这些剧今天不再派，今天也不会自动补派。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"拒绝本次 TikTok 派单失败：{ex.Message}");
        }
    }

    private async Task PauseDayAsync(HttpClient http, XingeRemoteConfig config, Action<string>? log, CancellationToken ct)
    {
        try
        {
            var envelope = await SendAsync<XingeApiEnvelope<JsonElement>>(http, config, HttpMethod.Post,
                "/client-api/tiktok/auto-publish/pause-day",
                new { client_id = config.ClientId, client_token = config.ClientToken }, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(envelope.BusinessDate))
                config.DispatchPauseDate = envelope.BusinessDate;
            else if (!string.IsNullOrWhiteSpace(envelope.DispatchPauseDate))
                config.DispatchPauseDate = envelope.DispatchPauseDate;
            else
                config.DispatchPauseDate = "paused";
            log?.Invoke("今日不再接收 TikTok 自动派单，尚未开始的剧已释放。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"今日不再接收 TikTok 派单失败：{ex.Message}");
        }
    }

    private async Task HandleCommandAsync(
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
            ct.ThrowIfCancellationRequested();
            result = await executeAsync(command with { XingeConnectionScope = XingeRuntimeConnection.ScopeForClient(config.BaseUrl, config.ClientId) }).ConfigureAwait(false);
        }

        try
        {
            await CompleteAsync(http, config, message.ID, result, ct).ConfigureAwait(false);
        }
        catch (XingeRemoteAuthenticationException) when (config.CanProvision)
        {
            await ProvisionAndSaveAsync(config, ct).ConfigureAwait(false);
            await CompleteAsync(http, config, message.ID, result, ct).ConfigureAwait(false);
        }
        log?.Invoke($"XINGE 远程命令完成：{result.SummaryText}");
        if (string.Equals(result.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
            (result.SummaryText ?? "").Contains("当前已有 TikTok 队列在执行", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ResumeStoppedAsync(
        HttpClient http,
        XingeRemoteConfig config,
        IReadOnlyList<long> assignmentIds,
        Action<string>? log,
        CancellationToken ct)
    {
        var ids = assignmentIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return true;
        try
        {
            await SendAsync<XingeApiEnvelope<JsonElement>>(http, config, HttpMethod.Post,
                "/client-api/tiktok/auto-publish/resume-stopped",
                new { client_id = config.ClientId, client_token = config.ClientToken, assignment_ids = ids }, ct).ConfigureAwait(false);
            log?.Invoke($"已把 {ids.Length} 部手动停止的剧占回，重启后继续执行，不会另派新剧。");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"占回手动停止的剧失败：{ex.Message}");
            return false;
        }
    }

    private static async Task<IReadOnlyList<XingeOpenAutoPublishAssignment>> ListOpenAutoPublishAsync(
        HttpClient http,
        XingeRemoteConfig config,
        CancellationToken ct)
    {
        var envelope = await SendAsync<XingeApiEnvelope<XingeOpenAutoPublishList>>(
            http,
            config,
            HttpMethod.Get,
            "/client-api/tiktok/auto-publish/open",
            null,
            ct).ConfigureAwait(false);
        if (!envelope.Ok)
            throw new InvalidOperationException(FirstNonEmpty(envelope.Message, "open assignments failed"));
        return envelope.Data?.Items ?? [];
    }

    private static async Task FlushAutoPublishProgressAsync(Action<string>? log, CancellationToken ct)
    {
        try
        {
            await XingeAutoPublishProgressReporter.Shared.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"XINGE 自动上新进度补发失败：{ex.Message}");
        }

        try
        {
            await XingeUploadTaskReporter.Shared.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"XINGE 上传任务补发失败：{ex.Message}");
        }
    }

    private async Task ProvisionAndSaveAsync(XingeRemoteConfig config, CancellationToken ct)
    {
        if (config.SoftwareSession is { } session)
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
            var automatic = await new XingeRemoteAccountService(http)
                .ProvisionFromSoftwareSessionAsync(session, config.ClientName, ct, config.Settings).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (XingeRuntimeConnection.Accept(session, automatic) is null)
                throw new InvalidOperationException("软件登录已变化，请重新连接 XINGE");
            config.ApplyCredentials(automatic);
            return;
        }

        var credentials = await XingeRemoteAccountService
            .ProvisionAsync(config.Settings, ct)
            .ConfigureAwait(false);

        var persisted = ClientSettingsStore.Load();
        ct.ThrowIfCancellationRequested();
        if (XingeRuntimeConnection.Mode(persisted) != XingeRuntimeConnection.Manual ||
            XingeRemoteAccountService.ComputeCredentialFingerprint(persisted) != credentials.CredentialFingerprint)
            throw new InvalidOperationException("手动连接配置已变化，请重新连接 XINGE");
        persisted.XingeClientId = credentials.ClientId;
        persisted.XingeClientToken = credentials.ClientToken;
        persisted.XingeCredentialFingerprint = credentials.CredentialFingerprint;
        ClientSettingsStore.Save(persisted);
        config.ApplyCredentials(credentials);
        Notify($"XINGE 账号已登录：{credentials.Username}");
    }

    private static async Task<bool> SyncProcessedSeriesAsync(
        HttpClient http,
        XingeRemoteConfig config,
        IReadOnlyList<XingeTikTokProcessedFact>? facts,
        HashSet<XingeTikTokProcessedFact> sent,
        Action<string>? log,
        CancellationToken ct)
    {
        if (facts is null)
            return true;
        foreach (var batch in facts.Where(fact => !sent.Contains(fact)).Chunk(100))
        {
            try
            {
                var envelope = await SendAsync<XingeApiEnvelope<JsonElement>>(http, config, HttpMethod.Post,
                    "/client-api/tiktok/processed-series", new { items = batch }, ct).ConfigureAwait(false);
                if (!envelope.Ok)
                    throw new InvalidOperationException("历史同步失败");
                sent.UnionWith(batch);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
            {
                log?.Invoke("XINGE 上传和归档历史暂未同步，下次连接将重试。");
                return false;
            }
        }
        return true;
    }

    private static async Task CompleteAsync(
        HttpClient http,
        XingeRemoteConfig config,
        long messageID,
        TikTokRemoteCommandResult result,
        CancellationToken ct)
    {
        var status = (result.Status ?? "").Trim().ToLowerInvariant() switch
        {
            "failed" => "failed",
            "accepted" => "accepted",
            "success" => "success",
            "duplicate_skipped" => "duplicate_skipped",
            _ => "failed",
        };
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
                ["processed_series"] = result.ProcessedSeries,
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
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new XingeRemoteAuthenticationException($"HTTP {(int)response.StatusCode}: {preview}");
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {preview}");
        }

        var parsed = JsonSerializer.Deserialize<T>(text, JsonOptions);
        return parsed ?? throw new InvalidOperationException("empty response");
    }

    private void Notify(string message) => StatusChanged?.Invoke(message);

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "")
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private sealed class XingeRemoteConfig
    {
        private XingeRemoteConfig(ClientSettings settings, string mode, XingeSoftwareSession? session, string baseUrl, TimeSpan pollInterval)
        {
            Settings = settings;
            Mode = mode;
            SoftwareSession = session;
            BaseUrl = baseUrl;
            var credentials = session is null ? XingeRuntimeConnection.Resolve(settings) : XingeRuntimeConnection.ForSession(session);
            ClientId = credentials?.ClientId ?? "";
            ClientToken = credentials?.ClientToken ?? "";
            ClientName = FirstNonEmpty(settings.XingeClientName, "TikTokPublisher");
            PollInterval = pollInterval;
        }

        public ClientSettings Settings { get; }
        public string Mode { get; }
        public XingeSoftwareSession? SoftwareSession { get; }
        public DispatchConfirmCoordinator Confirm { get; } = new();
        public Dictionary<long, XingeRemoteMessage> HeldConfirmMessages { get; } = new();
        public bool StoppedReclaimDone { get; set; }
        public string DispatchPauseDate { get; set; } = "";
        public string BaseUrl { get; }
        public string ClientId { get; private set; }
        public string ClientToken { get; private set; }
        public string ClientName { get; }
        public TimeSpan PollInterval { get; }
        public bool CanProvision => SoftwareSession is not null || (Mode == XingeRuntimeConnection.Manual && XingeRemoteAccountService.HasPasswordCredentials(Settings));
        public bool NeedsProvisioning => SoftwareSession is not null ? !HasClientCredentials
            : Mode == XingeRuntimeConnection.Manual && XingeRemoteAccountService.NeedsProvisioning(Settings);
        public bool HasClientCredentials =>
            !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientToken);

        public bool IsReady =>
            Mode != XingeRuntimeConnection.Off && !string.IsNullOrWhiteSpace(BaseUrl) &&
            (HasClientCredentials || CanProvision);

        public static XingeRemoteConfig FromSettings(ClientSettings settings)
        {
            var mode = XingeRuntimeConnection.Mode(settings);
            var session = mode == XingeRuntimeConnection.FollowLogin ? XingeRuntimeConnection.ReadSoftwareSession() : null;
            var baseUrl = mode == XingeRuntimeConnection.Manual
                ? XingeRuntimeConnection.NormalizeBaseUrl(FirstNonEmpty(settings.XingeServerUrl, settings.AuthServerUrl))
                : session?.BaseUrl ?? "";
            var intervalSeconds = ClientSettingsDefaults.NormalizeXingePollIntervalSeconds(settings.XingePollIntervalSeconds);
            return new XingeRemoteConfig(settings, mode, session, baseUrl, TimeSpan.FromSeconds(intervalSeconds));
        }

        public void ApplyCredentials(XingeRemoteClientCredentials credentials)
        {
            ClientId = credentials.ClientId;
            ClientToken = credentials.ClientToken;
            Settings.XingeClientId = credentials.ClientId;
            Settings.XingeClientToken = credentials.ClientToken;
            Settings.XingeCredentialFingerprint = credentials.CredentialFingerprint;
        }

    }

    private sealed class XingeRemoteAuthenticationException(string message) : Exception(message);

    public sealed class XingeOpenAutoPublishAssignment
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

        [JsonPropertyName("account_profile_name")]
        public string AccountProfileName { get; set; } = "";

        [JsonPropertyName("enabled_steps")]
        public List<string> EnabledSteps { get; set; } = [];

        [JsonPropertyName("queue_options")]
        public Dictionary<string, JsonElement>? QueueOptions { get; set; }

        [JsonPropertyName("command")]
        public JsonElement Command { get; set; }

        [JsonPropertyName("message_status")]
        public string MessageStatus { get; set; } = "";

        public TikTokRemoteCommand? ReadCommand()
        {
            if (Command.ValueKind != JsonValueKind.Object)
                return null;
            return TikTokRemoteCommandParser.Parse(Command.GetRawText());
        }

        public IReadOnlyDictionary<string, object?>? ReadQueueOptions()
        {
            if (QueueOptions is not { Count: > 0 })
                return null;
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in QueueOptions)
            {
                map[key] = value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                    _ => value.GetRawText(),
                };
            }

            return map;
        }
    }

    private sealed class XingeOpenAutoPublishList
    {
        [JsonPropertyName("items")]
        public List<XingeOpenAutoPublishAssignment> Items { get; set; } = [];
    }

    private sealed class XingeApiEnvelope<T>
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("data")]
        public T? Data { get; set; }

        [JsonPropertyName("quota_full_accounts")]
        public List<XingeQuotaFullAccount>? QuotaFullAccounts { get; set; }

        [JsonPropertyName("dispatch_syncing")]
        public bool DispatchSyncing { get; set; }

        [JsonPropertyName("dispatch_paused")]
        public bool DispatchPaused { get; set; }

        [JsonPropertyName("dispatch_pause_date")]
        public string DispatchPauseDate { get; set; } = "";

        [JsonPropertyName("business_date")]
        public string BusinessDate { get; set; } = "";

        [JsonPropertyName("dispatch_mode")]
        public string DispatchMode { get; set; } = "";
    }

    private sealed class XingeQuotaFullAccount
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("until")]
        public string Until { get; set; } = "";
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
