using ShortDrama.Core.Interfaces;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouPersonalActor(string Name, string Gender, string Role);

public sealed record KuaishouPersonalProjectData(
    string SourceDirectory,
    string WorkflowDirectory,
    string Title,
    string Intro,
    string ShortTitle,
    IReadOnlyList<string> Tags,
    string HorizontalCoverPath,
    string VerticalCoverPath,
    string CommitmentPdfPath,
    IReadOnlyList<string> ProjectImagePaths,
    IReadOnlyList<string> VideoPaths,
    IReadOnlyList<KuaishouPersonalActor> Actors);

public sealed class KuaishouPersonalProjectDataService
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private readonly IProjectScanner _projectScanner;
    public KuaishouPersonalProjectDataService(IProjectScanner projectScanner) => _projectScanner = projectScanner;

    public async Task<KuaishouPersonalProjectData> ResolveAsync(
        string projectDirectory,
        KuaishouPersonalConfig config,
        CancellationToken cancellationToken)
    {
        var selected = Path.GetFullPath(projectDirectory);
        var source = selected;
        var workflow = selected;
        if (!File.Exists(Path.Combine(selected, "短剧信息.txt")))
        {
            var root = Directory.GetParent(selected)?.FullName
                       ?? throw new InvalidOperationException("无法确定快手个人版项目根目录。");
            var scan = await _projectScanner.ScanAsync(root, null, cancellationToken);
            var project = scan.Projects.FirstOrDefault(item =>
                string.Equals(Path.GetFullPath(item.SourceProjectDir), selected, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.WorkflowProjectDir) &&
                 string.Equals(Path.GetFullPath(item.WorkflowProjectDir), selected, StringComparison.OrdinalIgnoreCase)))
                          ?? throw new InvalidOperationException("扫描结果中未找到当前项目。");
            source = Path.GetFullPath(project.SourceProjectDir);
            workflow = !string.IsNullOrWhiteSpace(project.WorkflowProjectDir)
                ? Path.GetFullPath(project.WorkflowProjectDir)
                : throw new InvalidOperationException("项目尚未生成 workflow 目录，请先执行素材准备步骤。");
        }

        var info = ParseInfo(Path.Combine(workflow, "短剧信息.txt"));
        var title = First(info.GetValueOrDefault("新剧名"), info.GetValueOrDefault("剧名"), info.GetValueOrDefault("原剧名"), Path.GetFileName(workflow).TrimStart('_'));
        var intro = First(info.GetValueOrDefault("简介"), info.GetValueOrDefault("剧情简介"), info.GetValueOrDefault("剧情"), info.GetValueOrDefault("介绍"));
        if (string.IsNullOrWhiteSpace(intro)) intro = $"《{title}》精彩剧情，人物关系与故事冲突逐步展开。";
        var videos = EnumerateVideos(Path.Combine(workflow, "videos"));
        if (videos.Count == 0) videos = EnumerateVideos(source);
        var horizontalCover = FindFirst(workflow, "快手横屏封面.*", "横屏封面.*", "海报图片.*");
        var verticalCover = FindFirst(workflow, "快手竖屏海报.*", "竖屏封面.*", "海报图片.*");
        var projectImages = Directory.EnumerateFiles(workflow, "工程图_*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        var commitment = FindFirst(workflow, "*承诺函*.pdf");
        if (string.IsNullOrWhiteSpace(commitment)) commitment = config.CommitmentPdfPath;
        return new KuaishouPersonalProjectData(
            source,
            workflow,
            title.Length > 30 ? title[..30] : title,
            intro,
            First(info.GetValueOrDefault("短标题"), title),
            Split(info.GetValueOrDefault("标签")).Take(5).ToArray(),
            horizontalCover,
            verticalCover,
            commitment,
            projectImages,
            videos,
            ParseActors(config.Actors));
    }

    private static Dictionary<string, string> ParseInfo(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOfAny([':', '：']);
            if (index > 0) result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return result;
    }

    private static IReadOnlyList<string> EnumerateVideos(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => EpisodeNumber(path)).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    private static int EpisodeNumber(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(path), @"第\s*0*(\d+)\s*集");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : int.MaxValue;
    }

    private static string FindFirst(string directory, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var path = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (path is not null) return path;
        }
        return string.Empty;
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split([',', '，', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<KuaishouPersonalActor> ParseActors(string? value)
    {
        var actors = new List<KuaishouPersonalActor>();
        foreach (var item in (value ?? string.Empty).Split([';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split([':', '：'], StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) continue;
            actors.Add(new KuaishouPersonalActor(parts[0], parts.ElementAtOrDefault(1) ?? "男", parts.ElementAtOrDefault(2) ?? "演员"));
        }
        return actors;
    }

    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
