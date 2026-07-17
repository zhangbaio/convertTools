using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChannelsPublisher.Prep;

/// <summary>扫描到的一条素材。CoverPath/DramaTitle 由带 manifest 的来源填充。</summary>
public sealed record ScannedMaterial(string VideoPath, string Title, string BaseDescription)
{
    public string? CoverPath { get; init; }
    public string? DramaTitle { get; init; }
    public int Slot { get; init; }
}

/// <summary>来源扫描：支持 目录 / 新剧挂载 / 系统高光下载 三种来源，规则移植自现有 Python。</summary>
public sealed class SourceScanner
{
    // 与 Python publish_video_source_service.PUBLISH_VIDEO_FILE_SUFFIXES 一致
    private static readonly string[] VideoExts = { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".flv", ".wmv", ".webm" };
    private static readonly string[] CoverSuffixes = { ".cover.jpg", ".cover.jpeg", ".cover.png", ".cover.webp" };
    private const string HighlightManifestName = ".system-highlight-download.json";

    public IReadOnlyList<ScannedMaterial> Scan(PrepConfig cfg)
    {
        var type = (cfg.SourceType ?? "directory").Trim().ToLowerInvariant();
        return type switch
        {
            "new_drama_mount" or "newdramamount" => ScanNewDramaMount(cfg.SourceDir),
            // 系统高光下载 与 下载素材视频 磁盘格式相同（视频 + .publish.json + .cover.* + manifest）
            "downloaded_system_highlight" or "material_video_download" or "highlight" => ScanDownloadedHighlight(cfg.SourceDir),
            "material_clips" or "clips" => ScanMaterialClips(cfg.SourceDir),
            "project_materials" or "project" => ScanFlat(SubDirCandidates(cfg.SourceDir, "material-videos")),
            "source_videos" or "source" => ScanFlat(new[] { cfg.SourceDir }),
            "custom_files" or "custom" => ScanCustomFiles(cfg),
            "directory_publish" or "dir_publish" => ScanDirectoryPublish(cfg.SourceDir),
            _ => ScanDirectory(cfg.SourceDir),
        };
    }

    // 兼容旧调用（目录扫描）
    public IReadOnlyList<ScannedMaterial> Scan(string sourceDir) => ScanDirectory(sourceDir);

    // ── 目录：递归找视频，自然排序，读同名/旁车描述 ──
    private IReadOnlyList<ScannedMaterial> ScanDirectory(string sourceDir)
    {
        if (!DirOk(sourceDir)) return Array.Empty<ScannedMaterial>();
        var videos = EnumerateVideos(sourceDir, recursive: true);
        var list = new List<ScannedMaterial>(videos.Count);
        int slot = 0;
        foreach (var v in videos)
            list.Add(new ScannedMaterial(v, Path.GetFileNameWithoutExtension(v), ReadDescription(v)) { Slot = ++slot });
        return list;
    }

    // ── 新剧挂载：标题取 shortdrama-project.json，视频递归自然排序 ──
    private IReadOnlyList<ScannedMaterial> ScanNewDramaMount(string projectDir)
    {
        if (!DirOk(projectDir)) return Array.Empty<ScannedMaterial>();
        var meta = LoadJson(Path.Combine(projectDir, "shortdrama-project.json"));
        var dramaTitle = FirstString(meta, "displayName", "title", "name", "newTitle", "new_title")
                         ?? new DirectoryInfo(projectDir).Name.TrimStart('_');

        var videos = EnumerateVideos(projectDir, recursive: true);
        var list = new List<ScannedMaterial>(videos.Count);
        int slot = 0;
        foreach (var v in videos)
            list.Add(new ScannedMaterial(v, Path.GetFileNameWithoutExtension(v), ReadDescription(v))
            {
                DramaTitle = dramaTitle,
                Slot = ++slot,
            });
        return list;
    }

    // ── 系统高光下载：直下视频 + <stem>.publish.json 旁车 + <stem>.cover.* + manifest 剧名 ──
    private IReadOnlyList<ScannedMaterial> ScanDownloadedHighlight(string sourceDir)
    {
        if (!DirOk(sourceDir)) return Array.Empty<ScannedMaterial>();
        var manifest = LoadJson(Path.Combine(sourceDir, HighlightManifestName));
        var dramaTitle = FirstString(manifest, "drama_title", "title") ?? new DirectoryInfo(sourceDir).Name;

        var videos = EnumerateVideos(sourceDir, recursive: false); // 仅本层，与 Python iterdir 一致
        var list = new List<ScannedMaterial>(videos.Count);
        int slot = 0;
        foreach (var v in videos)
        {
            slot++;
            var sidecar = LoadJson(Path.ChangeExtension(v, null) + ".publish.json");
            var title = FirstString(sidecar, "title", "shortTitle") ?? Path.GetFileNameWithoutExtension(v);
            var desc = FirstString(sidecar, "description", "caption") ?? "";
            list.Add(new ScannedMaterial(v, title, desc)
            {
                CoverPath = ResolveHighlightCover(v),
                DramaTitle = dramaTitle,
                Slot = slot,
            });
        }
        return list;
    }

    // ── 剪辑成片：<sourceDir>/素材剪辑输出 或 material-clip-output（本层） ──
    private IReadOnlyList<ScannedMaterial> ScanMaterialClips(string sourceDir)
        => ScanFlat(SubDirCandidates(sourceDir, "素材剪辑输出", "material-clip-output"));

    // ── 通用「若干候选目录的本层视频」扫描（项目素材/源视频/剪辑成片共用），跨目录去重 ──
    private IReadOnlyList<ScannedMaterial> ScanFlat(IEnumerable<string> roots)
    {
        var list = new List<ScannedMaterial>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int slot = 0;
        foreach (var root in roots)
        {
            if (!DirOk(root)) continue;
            foreach (var v in EnumerateVideos(root, recursive: false))
            {
                if (!seen.Add(Path.GetFullPath(v))) continue;
                list.Add(new ScannedMaterial(v, Path.GetFileNameWithoutExtension(v), ReadDescription(v)) { Slot = ++slot });
            }
        }
        return list;
    }

    // ── 自选视频：显式文件列表，过滤视频后缀，自然排序 ──
    private IReadOnlyList<ScannedMaterial> ScanCustomFiles(PrepConfig cfg)
    {
        var files = (cfg.CustomFiles ?? new List<string>())
            .Select(s => (s ?? "").Trim())
            .Where(s => s.Length > 0 && VideoExts.Contains(Path.GetExtension(s).ToLowerInvariant()) && File.Exists(s))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileNameWithoutExtension, NaturalComparer.Instance)
            .ToList();
        var list = new List<ScannedMaterial>(files.Count);
        int slot = 0;
        foreach (var v in files)
            list.Add(new ScannedMaterial(v, Path.GetFileNameWithoutExtension(v), ReadDescription(v)) { Slot = ++slot });
        return list;
    }

    // ── 目录批量发表：一级子目录，每个取最大视频 + description.txt/desc.txt/描述.txt（或文件夹名） ──
    private IReadOnlyList<ScannedMaterial> ScanDirectoryPublish(string root)
    {
        if (!DirOk(root)) return Array.Empty<ScannedMaterial>();
        var list = new List<ScannedMaterial>();
        int slot = 0;
        foreach (var sub in Directory.EnumerateDirectories(root).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var video = PickLargestVideo(sub);
            if (video == null) continue;
            var desc = NormalizeHashtags(ResolveSubdirDescription(sub));
            list.Add(new ScannedMaterial(video, Path.GetFileNameWithoutExtension(video), desc) { Slot = ++slot });
        }
        return list;
    }

    private static string? PickLargestVideo(string folder)
        => EnumerateVideos(folder, recursive: false)
            .Select(f => (f, len: SafeLen(f)))
            .OrderByDescending(x => x.len)
            .Select(x => x.f)
            .FirstOrDefault();

    private static long SafeLen(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

    private static string ResolveSubdirDescription(string sub)
    {
        foreach (var name in new[] { "description.txt", "desc.txt", "描述.txt" })
        {
            var p = Path.Combine(sub, name);
            if (File.Exists(p)) { var t = SafeRead(p); if (t.Length > 0) return t; }
        }
        return new DirectoryInfo(sub).Name; // 回退文件夹名
    }

    // #话题A#话题B → #话题A #话题B（在紧挨的 # 前补空格），移植 normalize_hashtags 的核心行为
    private static string NormalizeHashtags(string text)
        => string.IsNullOrEmpty(text) ? "" : Regex.Replace(text, @"(?<=\S)#", " #").Trim();

    // ── 辅助 ──
    private static string[] SubDirCandidates(string sourceDir, params string[] subs)
        => subs.Select(s => Path.Combine(sourceDir ?? "", s)).ToArray();

    private static bool DirOk(string d) => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d);

    private static List<string> EnumerateVideos(string dir, bool recursive)
        => Directory.EnumerateFiles(dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(Path.GetFileNameWithoutExtension, NaturalComparer.Instance)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? ResolveHighlightCover(string videoPath)
    {
        var noExt = Path.ChangeExtension(videoPath, null); // 去掉 .mp4 → <dir>/<stem>
        foreach (var suffix in CoverSuffixes)
        {
            var candidate = noExt + suffix; // <stem>.cover.jpg
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ReadDescription(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var txt = Path.Combine(dir, stem + ".txt");
        if (File.Exists(txt)) return SafeRead(txt);
        var sidecar = Path.Combine(dir, stem + ".publish.json");
        if (File.Exists(sidecar))
        {
            var d = FirstString(LoadJson(sidecar), "description", "caption");
            if (!string.IsNullOrEmpty(d)) return d!;
        }
        var folderTxt = Path.Combine(dir, "description.txt");
        return File.Exists(folderTxt) ? SafeRead(folderTxt) : "";
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch { return ""; }
    }

    private static Dictionary<string, JsonElement> LoadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new();
            var map = new Dictionary<string, JsonElement>();
            foreach (var p in doc.RootElement.EnumerateObject()) map[p.Name] = p.Value.Clone();
            return map;
        }
        catch { return new(); }
    }

    private static string? FirstString(Dictionary<string, JsonElement> map, params string[] keys)
    {
        foreach (var k in keys)
            if (map.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        return null;
    }
}

/// <summary>自然排序（第2集 排在 第10集 前面）。移植自 Python natural_video_sort_key。</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        var xa = Chunk(a ?? "");
        var xb = Chunk(b ?? "");
        int n = Math.Min(xa.Count, xb.Count);
        for (int i = 0; i < n; i++)
        {
            int c = Compare(xa[i], xb[i]);
            if (c != 0) return c;
        }
        return xa.Count.CompareTo(xb.Count);
    }

    private static int Compare((bool IsNum, long Num, string Str) a, (bool IsNum, long Num, string Str) b)
    {
        if (a.IsNum && b.IsNum) return a.Num.CompareTo(b.Num);
        if (a.IsNum != b.IsNum) return a.IsNum ? -1 : 1; // 数字段优先（与 Python (0,int) < (1,str) 一致）
        return string.CompareOrdinal(a.Str, b.Str);
    }

    private static List<(bool, long, string)> Chunk(string s)
    {
        var result = new List<(bool, long, string)>();
        foreach (Match m in Regex.Matches(s, @"\d+|\D+"))
        {
            var part = m.Value;
            if (part.Length == 0) continue;
            if (char.IsDigit(part[0]) && long.TryParse(part, out var num))
                result.Add((true, num, ""));
            else
                result.Add((false, 0, part.ToLowerInvariant()));
        }
        return result;
    }
}
