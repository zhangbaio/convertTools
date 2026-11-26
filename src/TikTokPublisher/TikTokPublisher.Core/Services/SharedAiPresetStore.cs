using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public enum SharedAiPresetKind { Text, Image }

public sealed record SharedAiPreset(
    string Id, SharedAiPresetKind Kind, string Name, string Endpoint, string Model,
    int Timeout = 60, string Ratio = "3:4");

/// <summary>本机两个发布平台共用的豆包预设。账号只保存预设 ID。</summary>
public static class SharedAiPresetStore
{
    private const string Prefix = "shared-ai:";

    private static string FilePath => Path.Combine(AppPaths.DataRoot, "shared-ai-presets.json");

    public static bool IsShared(string? id) =>
        id?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static IReadOnlyList<SharedAiPreset> List() => Read(FilePath).Values
        .Select(entry => entry.Preset)
        .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ToArray();

    public static SharedAiPreset Save(SharedAiPreset draft, string? apiKey)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("共享 AI 预设的 API Key 仅支持在 Windows 本机保存。");

        var endpoint = NormalizeEndpoint(draft.Endpoint);
        var preset = draft with
        {
            Id = draft.Id.Length == 0 ? Prefix + Guid.NewGuid().ToString("N") : draft.Id,
            Name = draft.Name.Trim(),
            Endpoint = endpoint,
            Model = draft.Model.Trim(),
            Ratio = PosterImageConfigHelper.NormalizeDoubaoImageRatio(draft.Ratio),
        };
        if (preset.Name.Length == 0 || preset.Model.Length == 0)
            throw new ArgumentException("请填写预设名称和模型 ID。");
        if (preset.Kind == SharedAiPresetKind.Text && preset.Timeout is < 5 or > 600)
            throw new ArgumentException("文本超时请填写 5 到 600 秒。");

        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        using var mutex = new Mutex(false, $@"Local\ShortDrama.SharedAiPresets.{identity}");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException("共享 AI 预设正在保存，请稍后重试。");
        }
        catch (AbandonedMutexException)
        {
            // The directory entry is replaced atomically, so the last committed file can be read.
        }

        try
        {
            var entries = Read(path);
            var previous = entries.GetValueOrDefault(preset.Id);
            if (draft.Id.Length > 0 && (previous is null || previous.Preset.Kind != preset.Kind))
                throw new ArgumentException("共享预设已变更，请刷新后重试。");

            var key = apiKey?.Trim();
            var protectedKey = previous?.ProtectedKey;
            if (!string.IsNullOrEmpty(key))
                protectedKey = Convert.ToBase64String(Protect(key, preset));
            else if (previous is null || !string.Equals(previous.Preset.Endpoint, endpoint, StringComparison.Ordinal)
                     || string.IsNullOrWhiteSpace(protectedKey))
                throw new ArgumentException("新建预设或变更接口地址时，请填写该地址的 API Key。");

            entries[preset.Id] = new(preset, protectedKey);
            Write(path, entries);
            return preset;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static bool ApplyText(ClientSettings settings, string? id)
    {
        if (!IsShared(id))
            return false;
        var (preset, key) = Resolve(id!, SharedAiPresetKind.Text);
        settings.AiTextEndpoint = preset.Endpoint;
        settings.AiTextModel = preset.Model;
        settings.AiTextTimeoutSeconds = preset.Timeout;
        settings.AiTextApiKey = key;
        return true;
    }

    public static bool ApplyImage(ClientSettings settings, string? id)
    {
        if (!IsShared(id))
            return false;
        var (preset, key) = Resolve(id!, SharedAiPresetKind.Image);
        settings.ImageProvider = "doubao";
        settings.ImageModelEndpoint = preset.Endpoint;
        settings.ImageModelId = preset.Model;
        settings.DoubaoImageResolution = "2K";
        settings.DoubaoImageRatio = preset.Ratio;
        settings.ImageModelApiKey = key;
        return true;
    }

    private static (SharedAiPreset Preset, string Key) Resolve(string id, SharedAiPresetKind kind)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("共享 AI 预设的 API Key 仅支持在 Windows 本机读取。");
        var label = kind == SharedAiPresetKind.Text ? "AI 文本" : "AI 图片";
        var entry = Read(FilePath).GetValueOrDefault(id);
        if (entry is null || entry.Preset.Kind != kind)
            throw new InvalidOperationException($"本机缺少所选的共享{label}预设，请在账号发布配置中重新选择。");

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(entry.ProtectedKey),
                Entropy(entry.Preset), DataProtectionScope.CurrentUser);
            try { return (entry.Preset, Encoding.UTF8.GetString(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException($"无法读取共享{label}预设的 API Key，请在系统设置中重新填写。", ex);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] Protect(string apiKey, SharedAiPreset preset)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        try { return ProtectedData.Protect(bytes, Entropy(preset), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Entropy(SharedAiPreset preset) =>
        Encoding.UTF8.GetBytes($"ShortDrama.SharedAiPresets\n{preset.Id}\n{preset.Kind}\n{preset.Endpoint}");

    private static string NormalizeEndpoint(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("https" or "http") ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("请输入有效的 HTTP(S) API 地址。");
        return endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static Dictionary<string, StoredPreset> Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<Dictionary<string, StoredPreset>>(stream) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("无法读取本机共享 AI 预设。", ex);
        }
    }

    private static void Write(string path, Dictionary<string, StoredPreset> entries)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record StoredPreset(SharedAiPreset Preset, string ProtectedKey);
}
