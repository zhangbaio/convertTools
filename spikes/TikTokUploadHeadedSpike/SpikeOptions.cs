using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokUploadHeadedSpike;

internal static class SpikeCover
{
    public static string Resolve(PublishItem item)
    {
        Action<string>? log = msg => Console.WriteLine($"[cover] {msg}");

        if (!string.IsNullOrWhiteSpace(item.CoverPath) && File.Exists(item.CoverPath))
        {
            var workflow = !string.IsNullOrWhiteSpace(item.ProjectDir)
                ? TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir)
                : "";
            if (!string.IsNullOrWhiteSpace(workflow))
                return TikTokCoverService.EnsureTikTok3x4Cover(item.CoverPath, workflow, log);
            return Path.GetFullPath(item.CoverPath);
        }

        var workflowDir = !string.IsNullOrWhiteSpace(item.ProjectDir)
            ? TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir)
            : "";
        var poster = TikTokCoverService.ResolvePosterPath(workflowDir, item.ProjectDir);
        if (!string.IsNullOrWhiteSpace(poster) && !string.IsNullOrWhiteSpace(workflowDir))
            return TikTokCoverService.EnsureTikTok3x4Cover(poster, workflowDir, log);

        var stem = Path.Combine(
            Path.GetDirectoryName(item.VideoPath) ?? "",
            Path.GetFileNameWithoutExtension(item.VideoPath));
        foreach (var candidate in new[] { stem + ".cover.jpg", stem + ".cover.png", stem + ".jpg", stem + ".png" })
        {
            if (!File.Exists(candidate)) continue;
            if (!string.IsNullOrWhiteSpace(workflowDir))
                return TikTokCoverService.EnsureTikTok3x4Cover(candidate, workflowDir, log);
            return Path.GetFullPath(candidate);
        }

        if (!string.IsNullOrWhiteSpace(item.ProjectDir))
        {
            foreach (var name in new[] { "海报图片.png", "海报图片.jpg", "cover.jpg", "cover.png", "poster.jpg", "poster.png" })
            {
                foreach (var root in new[] { workflowDir, item.ProjectDir })
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    var path = Path.Combine(root, name);
                    if (!File.Exists(path)) continue;
                    var wf = string.IsNullOrWhiteSpace(workflowDir) ? root : workflowDir;
                    return TikTokCoverService.EnsureTikTok3x4Cover(path, wf, log);
                }
            }
        }

        throw new InvalidOperationException(
            "未找到封面文件。请使用 --cover 指定，或在项目目录放置 海报图片.png / cover.jpg");
    }
}

internal static class SpikeHelp
{
    public static void Print()
    {
        Console.WriteLine("""
            TikTok 剧集上传内置浏览器验证（WebView2 + CDP）

            用法:
              TikTokUploadHeadedSpike [dom|fill|edit] [--account 1-2] [--workspace E:\tiktok2]
                [--row 35] [--title 深海] [--action none|draft|publish] [--cover path]
                [--max-videos N] [--auto-close]

            默认: fill 模式、只填不发、账号 1-2、工作目录取账号配置。
            使用应用内嵌 WebView2（与 TikTokPublisher 桌面版相同），不经外部 Chrome/Edge。
            dom 模式仅打开草稿页并导出 DOM/截图，不填表。
            fill/edit 走 EmbeddedBrowserPublishAutomation（新建/编辑含视频上传）。
            """);
    }
}

internal sealed class SpikeOptions
{
    public string Mode { get; init; } = "fill";
    public string AccountId { get; init; } = "1-2";
    public string? Workspace { get; init; }
    public int? RowIndex { get; init; }
    public string? TitleContains { get; init; }
    public FinalAction FinalAction { get; init; } = FinalAction.None;
    public bool ShowHelp { get; init; }
    public bool AutoClose { get; init; }
    public string? CoverOverride { get; init; }
    public int? MaxVideos { get; init; }

    public static SpikeOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
            return new SpikeOptions { ShowHelp = true };

        var mode = "fill";
        var accountId = "1-2";
        string? workspace = null;
        int? row = null;
        string? title = null;
        string? coverOverride = null;
        var action = FinalAction.None;
        var autoClose = false;
        int? maxVideos = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "dom":
                case "fill":
                case "edit":
                    mode = arg;
                    break;
                case "--account" when i + 1 < args.Length:
                    accountId = args[++i];
                    break;
                case "--workspace" when i + 1 < args.Length:
                    workspace = args[++i];
                    break;
                case "--row" when i + 1 < args.Length && int.TryParse(args[++i], out var r):
                    row = r;
                    break;
                case "--title" when i + 1 < args.Length:
                    title = args[++i];
                    break;
                case "--action" when i + 1 < args.Length:
                    action = args[++i].Trim().ToLowerInvariant() switch
                    {
                        "draft" => FinalAction.Draft,
                        "publish" => FinalAction.Publish,
                        _ => FinalAction.None,
                    };
                    break;
                case "--cover" when i + 1 < args.Length:
                    coverOverride = args[++i];
                    break;
                case "--auto-close":
                    autoClose = true;
                    break;
                case "--max-videos" when i + 1 < args.Length && int.TryParse(args[++i], out var mv):
                    maxVideos = mv;
                    break;
            }
        }

        return new SpikeOptions
        {
            Mode = mode,
            AccountId = accountId,
            Workspace = workspace,
            RowIndex = row,
            TitleContains = title,
            FinalAction = action,
            AutoClose = autoClose,
            CoverOverride = coverOverride,
            MaxVideos = maxVideos,
        };
    }
}
