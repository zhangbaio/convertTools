using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LicenseTranslate.Auth;

public sealed class AccountAuthClient
{
    private readonly HttpClient _http;

    public AccountAuthClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<AccountSession> LoginAsync(string serverUrl, string account, string password, CancellationToken cancellationToken = default)
    {
        var baseUrl = Clean(serverUrl);
        if (baseUrl.Length == 0) throw new AccountAuthException("请先填写授权服务地址");
        if (string.IsNullOrWhiteSpace(account)) throw new AccountAuthException("请输入用户名或邮箱");
        if (string.IsNullOrEmpty(password)) throw new AccountAuthException("请输入密码");
        var machineId = MachineFingerprint.Current();
        var payload = await PostAsync($"{baseUrl}/tt/account/login", new Dictionary<string, object?>
        {
            ["account"] = account.Trim(),
            ["password"] = password,
            ["machine_id"] = machineId,
            ["device_name"] = AccountProduct.DeviceName,
            ["app_name"] = AccountProduct.AppName,
            ["app_version"] = AccountProduct.AppVersion,
        }, cancellationToken);
        return Build(payload, machineId, baseUrl, account.Trim(), password);
    }

    public async Task<AccountSession> VerifyAsync(AccountSession session, CancellationToken cancellationToken = default)
    {
        var baseUrl = Clean(session.ServerUrl);
        if (baseUrl.Length == 0) throw new AccountAuthException("未配置授权服务地址");
        if (!session.IsSignedIn()) throw new AccountAuthException("当前没有可校验的登录信息");
        var machineId = MachineFingerprint.Current();
        if (!string.Equals(session.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
            throw new AccountRejectedException("登录状态与当前设备不匹配");
        try
        {
            var payload = await PostAsync($"{baseUrl}/tt/account/verify", new Dictionary<string, object?>
            {
                ["account"] = session.Account,
                ["machine_id"] = machineId,
                ["token"] = session.Token,
                ["device_name"] = AccountProduct.DeviceName,
                ["app_name"] = AccountProduct.AppName,
                ["app_version"] = AccountProduct.AppVersion,
            }, cancellationToken);
            var verified = Build(payload, machineId, baseUrl, session.Account, session.Password);
            verified.Password = session.Password;
            return verified;
        }
        catch (AccountRejectedException)
        {
            throw;
        }
    }

    public async Task LogoutAsync(AccountSession session, CancellationToken cancellationToken = default)
    {
        var baseUrl = Clean(session.ServerUrl);
        if (baseUrl.Length == 0 || string.IsNullOrWhiteSpace(session.Token))
            return;
        try
        {
            await PostAsync($"{baseUrl}/tt/account/logout", new Dictionary<string, object?>
            {
                ["machine_id"] = session.MachineId,
                ["token"] = session.Token,
            }, cancellationToken);
        }
        catch (AccountRejectedException)
        {
        }
    }

    private static AccountSession Build(Dictionary<string, JsonElement> payload, string machineId, string serverUrl, string account, string password)
    {
        var token = Read(payload, "token") ?? throw new AccountRejectedException("授权服务器未返回登录凭证");
        var ticket = Read(payload, "authorization_ticket") ?? throw new AccountRejectedException("授权服务器未返回签名票据");
        var identity = AccountTicketVerifier.VerifyFresh(ticket, machineId, token);
        return new AccountSession
        {
            ServerUrl = serverUrl,
            Account = identity.Subject.Length > 0 ? identity.Subject : account,
            Password = password,
            Token = token,
            MachineId = machineId,
            Email = identity.Email,
            ExpiresAt = identity.ExpiresAt,
            LastVerifiedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
    }

    private async Task<Dictionary<string, JsonElement>> PostAsync(string url, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(url, body, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = "授权服务返回错误：" + ErrorText(raw);
                if ((int)response.StatusCode is >= 400 and < 500)
                    throw new AccountRejectedException(message);
                if (response.StatusCode is HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                    throw new AccountNetworkException(message);
                throw new AccountAuthException(message);
            }
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new AccountAuthException("授权服务返回格式错误");
            var data = document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
            if (data.TryGetValue("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new AccountRejectedException(ErrorText(data));
            foreach (var key in new[] { "data", "result", "license" })
            {
                if (data.TryGetValue(key, out var nested) && nested.ValueKind == JsonValueKind.Object)
                    return nested.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
            }
            return data;
        }
        catch (AccountAuthException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new AccountNetworkException("网络连接授权服务失败：" + ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountNetworkException("连接授权服务超时：" + ex.Message);
        }
        catch (JsonException ex)
        {
            throw new AccountAuthException("授权服务返回了无法解析的 JSON：" + ex.Message);
        }
    }

    private static string? Read(Dictionary<string, JsonElement> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string ErrorText(object raw)
    {
        if (raw is Dictionary<string, JsonElement> data)
        {
            foreach (var key in new[] { "message", "error" })
            {
                if (data.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            }
        }
        if (raw is string text && text.Trim().Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return ErrorText(document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal));
            }
            catch (JsonException)
            {
                return text.Trim();
            }
            return text.Trim();
        }
        return "授权服务返回错误";
    }

    private static string Clean(string? serverUrl) => (serverUrl ?? "").Trim().TrimEnd('/');
}
