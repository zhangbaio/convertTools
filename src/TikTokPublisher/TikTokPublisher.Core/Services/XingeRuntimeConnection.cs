using System.Security.Cryptography;
using System.Text;
using PublisherShared.Licensing;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed class XingeSoftwareSession(string baseUrl, string account, string machineId, string token)
{
    public string BaseUrl { get; } = baseUrl;
    public string Account { get; } = account;
    public string MachineId { get; } = machineId;
    public string Token { get; } = token;

    public string SessionKey { get; } = XingeRuntimeConnection.HashIdentity(baseUrl, account.ToLowerInvariant(), machineId.ToLowerInvariant(), token);
}

public sealed class XingeConnectionCredentials(string baseUrl, string clientId, string clientToken, string scope)
{
    public string BaseUrl { get; } = baseUrl;
    public string ClientId { get; } = clientId;
    public string ClientToken { get; } = clientToken;
    public string Scope { get; } = scope;
}

/// <summary>Resolves the active connection for commands, material downloads and both reporters.</summary>
public static class XingeRuntimeConnection
{
    public const string FollowLogin = "follow_login";
    public const string Manual = "manual";
    public const string Off = "off";

    private static readonly object Sync = new();
    private static (string SessionKey, XingeConnectionCredentials Credentials)? _automatic;
    private static bool _manualAvailable = true;

    public static string Mode(ClientSettings settings) => settings.XingeConnectionMode switch
    {
        Manual => Manual,
        Off => Off,
        _ => FollowLogin,
    };

    public static XingeSoftwareSession? ReadSoftwareSession() => FromLicense(LicenseStore.Load());

    internal static XingeSoftwareSession? FromLicense(LicenseState state)
    {
        var baseUrl = NormalizeBaseUrl(state.ServerUrl);
        if (!state.IsActivated() || state.IsExpired() || baseUrl.Length == 0)
            return null;

        // The validated token and machine ID identify the account on XINGE even after a rename.
        return new XingeSoftwareSession(baseUrl, FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey),
            state.MachineId.Trim(), state.Token.Trim());
    }

    public static XingeConnectionCredentials? Resolve(ClientSettings settings)
    {
        switch (Mode(settings))
        {
            case Manual:
                if (!Volatile.Read(ref _manualAvailable))
                    return null;
                var baseUrl = NormalizeBaseUrl(FirstNonEmpty(settings.XingeServerUrl, settings.AuthServerUrl));
                var clientId = (settings.XingeClientId ?? "").Trim();
                var token = (settings.XingeClientToken ?? "").Trim();
                return baseUrl.Length > 0 && clientId.Length > 0 && token.Length > 0
                    ? new XingeConnectionCredentials(baseUrl, clientId, token, ScopeForClient(baseUrl, clientId))
                    : null;
            case FollowLogin:
                return ForSession(ReadSoftwareSession());
            default:
                return null;
        }
    }

    public static XingeConnectionCredentials? ForSession(XingeSoftwareSession? session)
    {
        lock (Sync)
        {
            if (_automatic is not { } cached || cached.SessionKey != session?.SessionKey)
            {
                _automatic = null;
                return null;
            }
            return cached.Credentials;
        }
    }

    public static XingeConnectionCredentials? Accept(XingeSoftwareSession session, XingeRemoteClientCredentials credentials)
    {
        lock (Sync)
        {
            if (Mode(ClientSettingsStore.Load()) != FollowLogin || ReadSoftwareSession()?.SessionKey != session.SessionKey)
                return null;

            var active = new XingeConnectionCredentials(session.BaseUrl, credentials.ClientId, credentials.ClientToken, ScopeForClient(session.BaseUrl, credentials.ClientId));
            _automatic = (session.SessionKey, active);
            return active;
        }
    }

    public static void ClearAutomatic()
    {
        lock (Sync)
            _automatic = null;
    }

    public static void SetManualAvailable(bool value) => Volatile.Write(ref _manualAvailable, value);

    public static string ScopeForClient(string baseUrl, string clientId) => HashIdentity(baseUrl, clientId);

    public static string NormalizeBaseUrl(string? value)
    {
        var text = (value ?? "").Trim().TrimEnd('/');
        if (text.Length == 0)
            return "";
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" &&
               string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment)
            ? url.AbsoluteUri.TrimEnd('/')
            : "";
    }

    internal static string HashIdentity(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values))));

    internal static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "").FirstOrDefault(value => value.Length > 0) ?? "";
}
