using System.Net.Http.Json;
using System.Text.Json;

namespace TikTokPublisher.Core.Licensing;

public class LicenseServiceException : Exception
{
    public LicenseServiceException(string message) : base(message) { }
}

public class LicenseNetworkException : LicenseServiceException
{
    public LicenseNetworkException(string message) : base(message) { }
}

public class LicenseRejectedException : LicenseServiceException
{
    public LicenseRejectedException(string message) : base(message) { }
}

public static class LicenseAuthService
{
    public const string AppName = "TikTok 短剧上传助手";
    public const string AppVersion = "0.1.0";
    public const string DeviceName = "TikTok Uploader Desktop";
    public const int VerifyIntervalHours = 1;
    public const int OfflineGraceHours = 72;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool ShouldVerify(LicenseState state)
    {
        if (string.IsNullOrWhiteSpace(state.LastVerifiedAt))
            return true;
        return !DateTimeOffset.TryParse(state.LastVerifiedAt, out var verified)
               || DateTimeOffset.Now >= verified.AddHours(VerifyIntervalHours);
    }

    public static bool IsAllowedOnThisMachine(LicenseState state)
    {
        var machineId = MachineFingerprintHelper.GetMachineFingerprint();
        var legacy = MachineFingerprintHelper.GetMachineFingerprintLegacy();
        return string.Equals(state.MachineId, machineId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state.MachineId, legacy, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOfflineGraceValid(LicenseState state)
    {
        if (!string.IsNullOrWhiteSpace(state.OfflineGraceUntil)
            && DateTimeOffset.TryParse(state.OfflineGraceUntil, out var graceUntil)
            && DateTimeOffset.Now < graceUntil)
            return true;

        if (string.IsNullOrWhiteSpace(state.LastVerifiedAt)
            || !DateTimeOffset.TryParse(state.LastVerifiedAt, out var verified))
            return false;
        return DateTimeOffset.Now < verified.AddHours(OfflineGraceHours);
    }

    public static LicenseState? LoadUsableState(
        string? serverUrl,
        bool verifyIfDue = true,
        bool allowOfflineGrace = true,
        bool forceVerify = false,
        string? account = null,
        string? password = null)
    {
        var state = LicenseStore.Load();
        if (!state.IsActivated() || !IsAllowedOnThisMachine(state) || state.IsExpired())
            return null;

        if (!forceVerify && (!verifyIfDue || !ShouldVerify(state)))
            return state;

        try
        {
            return VerifyStateWithCredentials(state, serverUrl, account, password);
        }
        catch (LicenseNetworkException)
        {
            return allowOfflineGrace && IsOfflineGraceValid(state) ? state : null;
        }
        catch (LicenseServiceException)
        {
            return null;
        }
    }

    public static async Task<LicenseState> LoginAsync(
        string serverUrl,
        string account,
        string password,
        CancellationToken ct = default)
    {
        var baseUrl = CleanBaseUrl(serverUrl);
        var machineId = MachineFingerprintHelper.GetMachineFingerprint();
        var state = await LoginCoreAsync(baseUrl, account, password, machineId, ct);
        LicenseStore.Save(state);
        return state;
    }

    public static LicenseState VerifyState(LicenseState state, string? serverUrl, CancellationToken ct = default) =>
        VerifyStateWithCredentials(state, serverUrl, account: null, password: null, ct);

    public static LicenseState VerifyStateWithCredentials(
        LicenseState state,
        string? serverUrl,
        string? account,
        string? password,
        CancellationToken ct = default)
    {
        var baseUrl = CleanBaseUrl(serverUrl ?? state.ServerUrl);
        if (baseUrl.Length == 0)
            throw new LicenseServiceException("未配置授权服务地址");
        if (!state.IsActivated())
            throw new LicenseServiceException("当前没有可校验的登录信息");

        var machineId = string.IsNullOrWhiteSpace(state.MachineId)
            ? MachineFingerprintHelper.GetMachineFingerprint()
            : state.MachineId.Trim();
        var loginAccount = FirstNonEmpty(account, state.AccountUsername, state.Email, state.LicenseKey);

        if (!string.IsNullOrWhiteSpace(loginAccount) && !string.IsNullOrEmpty(password))
        {
            try
            {
                var loginState = LoginCoreAsync(baseUrl, loginAccount, password!, machineId, ct)
                    .GetAwaiter()
                    .GetResult();
                LicenseStore.Save(loginState);
                return loginState;
            }
            catch (LicenseNetworkException)
            {
                throw;
            }
            catch (LicenseServiceException)
            {
                // Python 逻辑：保存态已激活时，账号密码登录失败后降级为 token 校验。
            }
        }

        try
        {
            var verified = VerifyByToken(state, baseUrl, machineId, ct);
            LicenseStore.Save(verified);
            return verified;
        }
        catch (LicenseRejectedException)
        {
            LicenseStore.Clear();
            throw;
        }
    }

    public static void Logout()
    {
        LicenseStore.Clear();
    }

    private static async Task<LicenseState> LoginCoreAsync(
        string baseUrl,
        string account,
        string password,
        string machineId,
        CancellationToken ct)
    {
        if (baseUrl.Length == 0)
            throw new LicenseServiceException("请先填写授权服务地址");
        if (string.IsNullOrWhiteSpace(account))
            throw new LicenseServiceException("请输入用户名或邮箱");
        if (string.IsNullOrEmpty(password))
            throw new LicenseServiceException("请输入密码");

        var payload = await PostJsonAsync(
            $"{baseUrl}/tt/account/login",
            new Dictionary<string, object?>
            {
                ["account"] = account.Trim(),
                ["password"] = password,
                ["machine_id"] = machineId,
                ["device_name"] = DeviceName,
                ["app_name"] = AppName,
                ["app_version"] = AppVersion,
            },
            ct);

        var result = EnsureSuccess(payload);
        var username = ReadString(result, "account_username", "username") ?? account.Trim();
        return BuildState(result, username, machineId, baseUrl);
    }

    private static LicenseState VerifyByToken(
        LicenseState state,
        string baseUrl,
        string machineId,
        CancellationToken ct)
    {
        var account = FirstNonEmpty(state.AccountUsername, state.LicenseKey);
        if (string.IsNullOrWhiteSpace(account))
            throw new LicenseServiceException("当前没有可校验的登录信息");

        var payload = PostJsonAsync(
            $"{baseUrl}/tt/account/verify",
            new Dictionary<string, object?>
            {
                ["account"] = account,
                ["machine_id"] = machineId,
                ["token"] = state.Token,
                ["device_name"] = DeviceName,
                ["app_name"] = AppName,
                ["app_version"] = AppVersion,
            },
            ct).GetAwaiter().GetResult();

        var result = EnsureSuccess(payload);
        var username = ReadString(result, "account_username", "username") ?? account;
        return BuildState(result, username, machineId, baseUrl, state);
    }

    private static LicenseState BuildState(
        Dictionary<string, JsonElement> payload,
        string licenseKey,
        string machineId,
        string serverUrl,
        LicenseState? current = null)
    {
        var baseState = current ?? new LicenseState();
        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        return new LicenseState
        {
            LicenseKey = licenseKey,
            LicenseKeyMasked = ReadString(payload, "license_key_masked") ?? LicenseStore.MaskLicenseKey(licenseKey),
            AccountUsername = ReadString(payload, "account_username", "username") ?? baseState.AccountUsername,
            Email = ReadString(payload, "email") ?? baseState.Email,
            MachineId = machineId,
            Token = ReadString(payload, "token") ?? baseState.Token,
            ActivatedAt = ReadString(payload, "activated_at") ?? baseState.ActivatedAt ?? now,
            LastVerifiedAt = ReadString(payload, "last_verified_at") ?? now,
            OfflineGraceUntil = ReadString(payload, "offline_grace_until") ?? baseState.OfflineGraceUntil,
            ExpiresAt = ReadString(payload, "expires_at") ?? baseState.ExpiresAt,
            Edition = ReadString(payload, "edition") ?? baseState.Edition,
            Licensee = ReadString(payload, "licensee") ?? baseState.Licensee,
            ServerUrl = serverUrl,
        };
    }

    private static async Task<Dictionary<string, JsonElement>> PostJsonAsync(
        string url,
        Dictionary<string, object?> body,
        CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(url, body, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"授权服务返回错误：{StringifyError(raw)}";
                if ((int)response.StatusCode is >= 400 and < 500)
                    throw new LicenseRejectedException(message);
                throw new LicenseServiceException(message);
            }

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new LicenseServiceException("授权服务返回格式错误");
            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        catch (LicenseServiceException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new LicenseNetworkException($"网络连接授权服务失败：{ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LicenseNetworkException($"连接授权服务超时：{ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new LicenseServiceException($"授权服务返回了无法解析的 JSON：{ex.Message}");
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            throw new LicenseNetworkException($"连接授权服务异常：{ex.Message}");
        }
    }

    private static Dictionary<string, JsonElement> EnsureSuccess(Dictionary<string, JsonElement> data)
    {
        if (data.TryGetValue("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new LicenseRejectedException(StringifyError(data));
        if (data.TryGetValue("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new LicenseRejectedException(StringifyError(data));

        foreach (var key in new[] { "data", "result", "license" })
        {
            if (data.TryGetValue(key, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                return nested.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            }
        }

        return data;
    }

    private static string StringifyError(object raw)
    {
        if (raw is Dictionary<string, JsonElement> dict)
        {
            foreach (var key in new[] { "message", "error" })
            {
                if (dict.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            }
        }

        if (raw is string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return trimmed;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var parsed = doc.RootElement.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                    return StringifyError(parsed);
                }
            }
            catch
            {
                // Raw service text is still useful to show.
            }

            return trimmed;
        }

        return raw.ToString() ?? "授权服务返回错误";
    }

    private static string? ReadString(Dictionary<string, JsonElement> payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "")
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private static string CleanBaseUrl(string? serverUrl) =>
        (serverUrl ?? "").Trim().TrimEnd('/');
}
