using System.Text.Json;

namespace TikTokPublisher.Core.Services;

internal readonly record struct CompanyName(string English);

internal readonly record struct CompanyLibrary(CompanyName[] Names, string? Notice);

public readonly record struct CompanyNameChoice(bool Exhausted, string English);

public static class CompanyNameLibraries
{
    public const string Template1ExhaustedMessage = "模板1的公司名称已全部随机生成过，请更换公司库后再试。";
    public const string Template2ExhaustedMessage = "模板2的公司名称已全部随机生成过，请更换公司库后再试。";

    public static CompanyNameChoice TakeTemplate1(Random? random = null) =>
        CompanyNameCatalog.Take("template1", CompanyNameCatalog.Mississippi, random ?? Random.Shared);

    public static CompanyNameChoice TakeTemplate2(Random? random = null) =>
        CompanyNameCatalog.Take("template2", CompanyNameCatalog.Kentucky, random ?? Random.Shared);

    public static bool TryImportTemplate1(string sourcePath, out string message) =>
        CompanyNameCatalog.TryImport("template1", sourcePath, null, true, true, out message);

    public static bool TryImportTemplate2(string sourcePath, out string message) =>
        CompanyNameCatalog.TryImport("template2", sourcePath, null, true, true, out message);

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

    private static CompanyLibrary MississippiLibrary = Open(Template1FileName);
    private static CompanyLibrary KentuckyLibrary = Open(Template2FileName);

    internal static CompanyName[] Mississippi => MississippiLibrary.Names;

    internal static CompanyName[] Kentucky => KentuckyLibrary.Names;

    internal static string? MississippiNotice => MississippiLibrary.Notice;

    internal static string? KentuckyNotice => KentuckyLibrary.Notice;

    internal static CompanyName PickMississippi(Random random) => Mississippi[random.Next(Mississippi.Length)];

    internal static CompanyName PickKentucky(Random random) => Kentucky[random.Next(Kentucky.Length)];

    internal static bool TryImport(
        string key,
        string sourcePath,
        string? userDirectory,
        bool replaceMemory,
        bool updateBesideApp,
        out string message)
    {
        var fileName = key == "template2" ? Template2FileName : Template1FileName;
        var label = key == "template2" ? "模板2" : "模板1";
        if (!TryRead(sourcePath, out var names, out var reason))
        {
            message = $"公司库无法使用（{reason}）。";
            return false;
        }

        var directory = userDirectory ?? UserDirectory;
        var json = JsonSerializer.Serialize(names.Select(item => item.English).ToArray(), WriteOptions);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), json);
        if (updateBesideApp)
        {
            var besideApp = Path.Combine(AppContext.BaseDirectory, "articles-of-incorporation", fileName);
            if (File.Exists(besideApp))
                File.WriteAllText(besideApp, json);
        }

        if (replaceMemory)
        {
            var library = new CompanyLibrary(names, null);
            if (key == "template2")
                KentuckyLibrary = library;
            else
                MississippiLibrary = library;
        }

        message = $"已导入{label}公司库，共 {names.Length} 个名称。";
        return true;
    }

    internal static void SetMississippi(CompanyLibrary library) => MississippiLibrary = library;

    internal static void SetKentucky(CompanyLibrary library) => KentuckyLibrary = library;

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
            return new CompanyNameChoice(true, previous.English ?? string.Empty);
        }

        var picked = available[random.Next(available.Length)];
        if (!used.Contains(Normalize(picked.English)))
            usedOrder.Add(picked.English);
        SaveUsedNames(path, state);
        return new CompanyNameChoice(false, picked.English);
    }

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
            using var document = JsonDocument.Parse(File.ReadAllText(path), JsonDocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                reason = "没有公司名称";
                return false;
            }

            var list = new List<CompanyName>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var english = ReadEnglish(entry);
                if (english.Length == 0)
                {
                    reason = "存在空的公司名称";
                    return false;
                }

                if (!seen.Add(english))
                    continue;

                list.Add(new CompanyName(english));
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

    private static string ReadEnglish(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
            return (entry.GetString() ?? string.Empty).Trim();

        if (entry.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (var property in entry.EnumerateObject())
        {
            if (property.Name.Equals("english", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return (property.Value.GetString() ?? string.Empty).Trim();
        }

        return string.Empty;
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

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class UsedNameFile
    {
        public List<string> Template1 { get; set; } = [];

        public List<string> Template2 { get; set; } = [];
    }
}
