using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TikTokPublisher.Core.Services;

public static class ClientFeaturePermissions
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static Snapshot _current = new("", new HashSet<string>());
    private sealed record Snapshot(string Scope, HashSet<string> Disabled);
    private sealed record FeatureResponse([property: JsonPropertyName("disabled_modules")] string[]? DisabledModules);

    public static bool Allows(string module) => !Volatile.Read(ref _current).Disabled.Contains(module);

    public static async Task RefreshAsync(CancellationToken ct)
    {
        if (!await RefreshGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;
        try
        {
            var session = XingeRuntimeConnection.ReadSoftwareSession();
            if (session is null)
                return;

            var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{session.BaseUrl}\n{session.Account.ToLowerInvariant()}")));
            var directory = Path.Combine(AppPaths.DataRoot, "feature-permissions");
            var path = Path.Combine(directory, scope + ".json");
            if (Volatile.Read(ref _current).Scope != scope)
            {
                var cached = File.Exists(path) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) : null;
                Volatile.Write(ref _current, new Snapshot(scope, new HashSet<string>(cached ?? [], StringComparer.Ordinal)));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, session.BaseUrl.TrimEnd('/') + "/client-api/account/features");
            request.Headers.TryAddWithoutValidation("X-Xinge-Account", session.Account);
            request.Headers.TryAddWithoutValidation("X-Xinge-Machine-ID", session.MachineId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || XingeRuntimeConnection.ReadSoftwareSession()?.SessionKey != session.SessionKey)
                return;
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize<FeatureResponse>(raw);
            if (value is null)
                return;
            var disabled = value.DisabledModules ?? [];
            Volatile.Write(ref _current, new Snapshot(scope, new HashSet<string>(disabled, StringComparer.Ordinal)));
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(disabled));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            // Keep the last successful permissions when the connection or local storage is unavailable.
        }
        finally
        {
            RefreshGate.Release();
        }
    }
}
