using System.Text.Json;
using System.Text.Json.Serialization;

namespace TikTokPublisher.Core.Services;

internal readonly record struct CompanyName(string English, string Chinese);

internal readonly record struct CompanyLibrary(CompanyName[] Names, string? Notice);

public readonly record struct CompanyNameChoice(bool Exhausted, string English, string Chinese);

public static class CompanyNameLibraries
{
    public const string Template1ExhaustedMessage = "模板1的公司名称已全部随机生成过，请更换公司库后再试。";
    public const string Template2ExhaustedMessage = "模板2的公司名称已全部随机生成过，请更换公司库后再试。";

    public static CompanyNameChoice TakeTemplate1(Random? random = null) =>
        CompanyNameCatalog.Take("template1", CompanyNameCatalog.Mississippi, random ?? Random.Shared);

    public static CompanyNameChoice TakeTemplate2(Random? random = null) =>
        CompanyNameCatalog.Take("template2", CompanyNameCatalog.Kentucky, random ?? Random.Shared);

    public static string? StartupNotice
    {
        get
        {
            var template1 = CompanyNameCatalog.MississippiNotice;
            var template2 = CompanyNameCatalog.KentuckyNotice;
            if (string.IsNullOrWhiteSpace(template1))
                return string.IsNullOrWhiteSpace(template2) ? null : template2;
            if (string.IsNullOrWhiteSpace(template2))
                return template1;
            return template1 + Environment.NewLine + template2;
        }
    }
}

internal static class CompanyNameCatalog
{
    internal const string Template1FileName = "company-names-template1.json";
    internal const string Template2FileName = "company-names-template2.json";

    private static readonly CompanyLibrary MississippiLibrary = Open(Template1FileName);
    private static readonly CompanyLibrary KentuckyLibrary = Open(Template2FileName);
    private static readonly Dictionary<string, string> MississippiByEnglish = Build(MississippiLibrary.Names);
    private static readonly Dictionary<string, string> KentuckyByEnglish = Build(KentuckyLibrary.Names);

    internal static CompanyName[] Mississippi => MississippiLibrary.Names;

    internal static CompanyName[] Kentucky => KentuckyLibrary.Names;

    internal static string? MississippiNotice => MississippiLibrary.Notice;

    internal static string? KentuckyNotice => KentuckyLibrary.Notice;

    internal static CompanyName PickMississippi(Random random) => Mississippi[random.Next(Mississippi.Length)];

    internal static CompanyName PickKentucky(Random random) => Kentucky[random.Next(Kentucky.Length)];

    internal static CompanyNameChoice Take(string key, CompanyName[] catalog, Random random, string? storePath = null)
    {
        var path = storePath ?? UsedNamesPath;
        var state = LoadUsedNames(path);
        var usedOrder = UsedList(state, key);
        var used = new HashSet<string>(usedOrder.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var available = catalog.Where(item => !used.Contains(Normalize(item.English))).ToArray();
        if (available.Length == 0)
        {
            var previous = LastInCatalog(usedOrder, catalog);
            return new CompanyNameChoice(true, previous.English ?? string.Empty, previous.Chinese ?? string.Empty);
        }

        var picked = available[random.Next(available.Length)];
        if (!used.Contains(Normalize(picked.English)))
            usedOrder.Add(picked.English);
        SaveUsedNames(path, state);
        return new CompanyNameChoice(false, picked.English, picked.Chinese);
    }

    internal static bool TryMississippiChinese(string? english, out string chinese) =>
        Try(MississippiByEnglish, english, out chinese);

    internal static bool TryKentuckyChinese(string? english, out string chinese) =>
        Try(KentuckyByEnglish, english, out chinese);

    internal static CompanyLibrary Open(string fileName, string? shippedDirectory = null, string? userDirectory = null)
    {
        var userPath = Path.Combine(userDirectory ?? UserDirectory, fileName);
        if (File.Exists(userPath) && TryRead(userPath, out var custom, out _))
            return new CompanyLibrary(custom, null);

        var shipped = ReadShipped(fileName, shippedDirectory);
        if (!File.Exists(userPath))
            return new CompanyLibrary(shipped, null);

        TryRead(userPath, out _, out var reason);
        return new CompanyLibrary(shipped, $"公司库 {fileName} 无法使用（{reason}），已改用程序自带库。");
    }

    private static CompanyName[] ReadShipped(string fileName, string? shippedDirectory)
    {
        var path = shippedDirectory is null
            ? ArticlesOfIncorporationPdfService.ResolveTemplatePath(fileName)
            : Path.Combine(shippedDirectory, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("未找到程序自带公司库。", path);
        if (!TryRead(path, out var names, out var reason))
            throw new InvalidDataException($"程序自带公司库 {fileName} 无法使用（{reason}）。");
        return names;
    }

    private static bool TryRead(string path, out CompanyName[] names, out string reason)
    {
        names = [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<CompanyNameEntry>>(File.ReadAllText(path), JsonOptions);
            if (entries is null || entries.Count == 0)
            {
                reason = "没有公司名称";
                return false;
            }

            var list = new List<CompanyName>(entries.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var english = (entry.English ?? string.Empty).Trim();
                var chinese = (entry.Chinese ?? string.Empty).Trim();
                if (english.Length == 0 || chinese.Length == 0)
                {
                    reason = "存在空的公司名称";
                    return false;
                }

                if (!seen.Add(english))
                {
                    reason = $"英文名重复：{english}";
                    return false;
                }

                list.Add(new CompanyName(english, chinese));
            }

            names = list.ToArray();
            reason = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            reason = "文件不是有效的 JSON";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "文件无法读取";
            return false;
        }
    }

    private static bool Try(Dictionary<string, string> map, string? english, out string chinese)
    {
        var key = (english ?? string.Empty).Trim();
        if (key.Length > 0 && map.TryGetValue(key, out var found))
        {
            chinese = found;
            return true;
        }

        chinese = string.Empty;
        return false;
    }

    private static Dictionary<string, string> Build(CompanyName[] names)
    {
        var map = new Dictionary<string, string>(names.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            map[name.English] = name.Chinese;
        return map;
    }

    private static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".registration_pdf");

    private static string UsedNamesPath => Path.Combine(UserDirectory, "used-company-names.json");

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static List<string> UsedList(UsedNameFile state, string key) =>
        key == "template2" ? state.Template2 : state.Template1;

    private static CompanyName LastInCatalog(IReadOnlyList<string> usedOrder, CompanyName[] catalog)
    {
        var byEnglish = new Dictionary<string, CompanyName>(catalog.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var name in catalog)
            byEnglish[Normalize(name.English)] = name;
        for (var index = usedOrder.Count - 1; index >= 0; index--)
        {
            if (byEnglish.TryGetValue(Normalize(usedOrder[index]), out var name))
                return name;
        }

        return default;
    }

    private static UsedNameFile LoadUsedNames(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new UsedNameFile();
            var state = JsonSerializer.Deserialize<UsedNameFile>(File.ReadAllText(path), JsonOptions) ?? new UsedNameFile();
            state.Template1 ??= [];
            state.Template2 ??= [];
            return state;
        }
        catch
        {
            return new UsedNameFile();
        }
    }

    private static void SaveUsedNames(string path, UsedNameFile state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            // Remembering a drawn name should not block the next random generation.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record CompanyNameEntry(
        [property: JsonPropertyName("english")] string? English,
        [property: JsonPropertyName("chinese")] string? Chinese);

    private sealed class UsedNameFile
    {
        public List<string> Template1 { get; set; } = [];

        public List<string> Template2 { get; set; } = [];
    }
}
