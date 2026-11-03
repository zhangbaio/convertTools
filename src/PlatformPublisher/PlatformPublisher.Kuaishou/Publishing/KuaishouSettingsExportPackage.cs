using System.IO.Compression;
using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouSettingsExportPackage : IDisposable
{
    public const string AppId = "weixin-channel-tool";

    private readonly JsonDocument _document;

    private KuaishouSettingsExportPackage(
        JsonDocument document,
        string exportMode,
        JsonElement settings,
        IReadOnlyList<JsonElement> profiles,
        IReadOnlyDictionary<string, byte[]> authFiles,
        IReadOnlyDictionary<string, ExportedKuaishouAsset> assetsByOriginalPath)
    {
        _document = document;
        ExportMode = exportMode;
        Settings = settings;
        Profiles = profiles;
        AuthFiles = authFiles;
        AssetsByOriginalPath = assetsByOriginalPath;
    }

    public string ExportMode { get; }
    public JsonElement Settings { get; }
    public IReadOnlyList<JsonElement> Profiles { get; }
    public IReadOnlyDictionary<string, byte[]> AuthFiles { get; }
    public IReadOnlyDictionary<string, ExportedKuaishouAsset> AssetsByOriginalPath { get; }

    public static KuaishouSettingsExportPackage Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException("找不到导出的配置文件。");
        return IsZip(fullPath) ? OpenZip(fullPath) : OpenJson(fullPath);
    }

    public byte[]? FindAuth(string profileId, bool personal)
    {
        var specificName = personal
            ? "kuaishou_personal_kdj_auth_state.json"
            : "kuaishou_enterprise_kdj_auth_state.json";
        if (AuthFiles.TryGetValue(AuthKey(profileId, specificName), out var specific))
            return specific;
        return AuthFiles.TryGetValue(AuthKey(profileId, "kuaishou_kdj_auth_state.json"), out var shared)
            ? shared
            : null;
    }

    public bool HasEditionData(JsonElement profile, bool personal)
    {
        var prefix = personal ? "kuaishou_personal_" : "kuaishou_enterprise_";
        foreach (var suffix in new[] { "username", "nickname", "last_workspace", "archive_root_dir", "commitment_pdf_path", "kuaishou_id" })
        {
            if (!string.IsNullOrWhiteSpace(Text(profile, prefix + suffix)))
                return true;
        }
        return FindAuth(Text(profile, "id") ?? "", personal) is not null;
    }

    public void Dispose() => _document.Dispose();

    public static string Text(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
            return "";
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : property.ToString();
    }

    private static KuaishouSettingsExportPackage OpenJson(string path)
    {
        var document = JsonDocument.Parse(File.ReadAllText(path));
        return Create(document, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, ExportedKuaishouAsset>(StringComparer.OrdinalIgnoreCase));
    }

    private static KuaishouSettingsExportPackage OpenZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var settingsEntry = archive.GetEntry("settings.json")
            ?? throw new InvalidOperationException("压缩包里没有 settings.json。");
        using var settingsStream = settingsEntry.Open();
        var document = JsonDocument.Parse(settingsStream);
        var auth = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith("data/auth/", StringComparison.OrdinalIgnoreCase) || name.Contains("..", StringComparison.Ordinal))
                continue;
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            auth[name["data/auth/".Length..]] = ReadEntry(entry);
        }

        var assets = new Dictionary<string, ExportedKuaishouAsset>(StringComparer.OrdinalIgnoreCase);
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is not null)
        {
            using var manifestStream = manifestEntry.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            if (manifest.RootElement.TryGetProperty("assets", out var assetList) && assetList.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetList.EnumerateArray())
                {
                    var archivePath = Text(asset, "archive_path").Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(archivePath) || archivePath.Contains("..", StringComparison.Ordinal))
                        continue;
                    var entry = archive.Entries.FirstOrDefault(item =>
                        string.Equals(item.FullName.Replace('\\', '/'), archivePath, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                        continue;
                    var bytes = ReadEntry(entry);
                    if (!asset.TryGetProperty("refs", out var refs) || refs.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var reference in refs.EnumerateArray())
                    {
                        var original = Text(reference, "path");
                        if (string.IsNullOrWhiteSpace(original))
                            continue;
                        assets[original] = new ExportedKuaishouAsset(entry.Name, bytes);
                    }
                }
            }
        }

        return Create(document, auth, assets);
    }

    private static KuaishouSettingsExportPackage Create(
        JsonDocument document,
        IReadOnlyDictionary<string, byte[]> authFiles,
        IReadOnlyDictionary<string, ExportedKuaishouAsset> assets)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("配置文件格式错误：根节点必须是对象。");
        var app = Text(root, "app");
        if (!string.Equals(app, AppId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("这不是微信工具导出的配置。");
        if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("配置文件格式错误：缺少 settings 对象。");
        var profiles = ReadProfiles(settings);
        return new KuaishouSettingsExportPackage(document, Text(root, "export_mode"), settings, profiles, authFiles, assets);
    }

    private static IReadOnlyList<JsonElement> ReadProfiles(JsonElement settings)
    {
        if (!settings.TryGetProperty("account_profiles_json", out var raw))
            return [];
        JsonDocument? owned = null;
        try
        {
            JsonElement array;
            if (raw.ValueKind == JsonValueKind.String)
            {
                owned = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw.GetString()) ? "[]" : raw.GetString()!);
                array = owned.RootElement;
            }
            else
            {
                array = raw;
            }
            if (array.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("账号档案不是列表。");
            return array.EnumerateArray().Select(item => item.Clone()).ToArray();
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static string AuthKey(string profileId, string fileName) =>
        "profiles/" + profileId.Trim().Trim('/') + "/" + fileName;

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool IsZip(string path)
    {
        Span<byte> magic = stackalloc byte[2];
        using var stream = File.OpenRead(path);
        return stream.Read(magic) == 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
    }
}

public sealed record ExportedKuaishouAsset(string FileName, byte[] Bytes);
