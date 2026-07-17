using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;

namespace TikTokUploadHeadedSpike;

internal sealed class SpikeHostContext
{
    public static SpikeHostContext? Current { get; set; }

    public required SpikeOptions Options { get; init; }
    public required TikTokAccountProfile Account { get; init; }
    public required string Workspace { get; init; }
    public required string LogPath { get; init; }
    public QueueProjectItem? Project { get; init; }
    public PublishItem Item { get; init; } = new();
    public TikTokPublishOptions PublishOptions { get; init; } = new();
    public TikTokPublishPayload Payload { get; init; } = new();
    public TikTokPublishRecommendation Recommendation { get; init; } = new();
    public string CoverPath { get; init; } = "";
    public string AuthPath { get; init; } = "";
    public int ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
}

internal static class SpikeBootstrap
{
    public static (SpikeHostContext? Context, int? ExitCode) Prepare(string[] args)
    {
        var opts = SpikeOptions.Parse(args);
        if (opts.ShowHelp)
        {
            SpikeHelp.Print();
            return (null, 0);
        }

        var store = new AccountStore();
        store.Load();
        var account = store.Accounts.FirstOrDefault(a => a.Id == opts.AccountId)
            ?? throw new InvalidOperationException($"未找到账号：{opts.AccountId}");

        var workspace = string.IsNullOrWhiteSpace(opts.Workspace)
            ? account.ResolveWorkspacePath()
            : Path.GetFullPath(opts.Workspace);
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            throw new InvalidOperationException($"工作目录不可用：{workspace ?? "(空)"}");

        var outDir = Path.Combine(AppContext.BaseDirectory, "embedded-run");
        Directory.CreateDirectory(outDir);
        var logPath = Path.Combine(outDir, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        QueueProjectItem? project = null;
        PublishItem item;
        TikTokPublishOptions options;
        TikTokPublishPayload payload;
        TikTokPublishRecommendation recommendation;
        string coverPath;

        if (opts.Mode != "dom")
        {
            var projects = WorkspaceQueueService.ScanProjects(workspace);
            if (projects.Count == 0)
                throw new InvalidOperationException($"工作目录无项目：{workspace}");

            if (opts.RowIndex is > 0)
            {
                var row = opts.RowIndex.Value;
                if (row < 1 || row > projects.Count)
                    throw new InvalidOperationException($"行号 {row} 超出范围（共 {projects.Count} 个项目）");
                project = projects[row - 1];
            }
            else if (!string.IsNullOrWhiteSpace(opts.TitleContains))
            {
                project = projects.FirstOrDefault(p =>
                    (p.Title ?? "").Contains(opts.TitleContains, StringComparison.OrdinalIgnoreCase)
                    || (p.OriginalTitle ?? "").Contains(opts.TitleContains, StringComparison.OrdinalIgnoreCase));
                if (project is null)
                    throw new InvalidOperationException($"未找到标题包含「{opts.TitleContains}」的项目");
            }
            else
            {
                project = WorkspaceQueueService.FilterPendingUpload(projects).FirstOrDefault()
                    ?? projects.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.PrimaryVideoPath))
                    ?? projects[0];
            }

            item = QueuePublishHost.ToPublishItem(project);
            if (string.IsNullOrWhiteSpace(item.VideoPath) || !File.Exists(item.VideoPath))
                throw new InvalidOperationException($"项目无可用视频：{project.DisplayName} ({project.ProjectDir})");

            options = TikTokPublishOptions.FromAccount(account);
            payload = TikTokPublishPayload.FromPublishItem(item);

            if (opts.MaxVideos is > 0)
            {
                var limited = payload.VideoPaths.Take(opts.MaxVideos.Value).ToList();
                if (limited.Count == 0)
                    throw new InvalidOperationException($"--max-videos {opts.MaxVideos} 但项目无可用视频文件。");
                var sourceEpisodeCount = payload.EpisodeCount;
                payload = new TikTokPublishPayload
                {
                    Title = payload.Title,
                    OriginalTitle = payload.OriginalTitle,
                    Description = payload.Description,
                    EpisodeCount = sourceEpisodeCount,
                    VideoPaths = limited,
                    UploadVideoPaths = limited,
                };
                item = new PublishItem
                {
                    VideoPath = limited[0],
                    Title = item.Title,
                    OriginalTitle = item.OriginalTitle,
                    Description = item.Description,
                    EpisodeCount = sourceEpisodeCount,
                    GenreCategory = item.GenreCategory,
                    CoverPath = item.CoverPath,
                    ProjectDir = item.ProjectDir,
                };
            }

            var projectPayload = TikTokProjectPayloadFactory.BuildFromPublishItem(item);
            var settings = ClientSettingsStore.Load();
            recommendation = TikTokPublishRecommendationService.BuildRecommendationAsync(
                projectPayload,
                settings,
                options,
                msg => File.AppendAllText(logPath, $"[publish] {msg}{Environment.NewLine}"),
                CancellationToken.None).GetAwaiter().GetResult();
            coverPath = SpikeCover.Resolve(item);
            if (!string.IsNullOrWhiteSpace(opts.CoverOverride))
                coverPath = Path.GetFullPath(opts.CoverOverride);
        }
        else
        {
            item = new PublishItem();
            options = TikTokPublishOptions.FromAccount(account);
            payload = new TikTokPublishPayload();
            recommendation = options.BuildRecommendation(item);
            coverPath = "";
        }

        var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
        var ctx = new SpikeHostContext
        {
            Options = opts,
            Account = account,
            Workspace = workspace,
            LogPath = logPath,
            Project = project,
            Item = item,
            PublishOptions = options,
            Payload = payload,
            Recommendation = recommendation,
            CoverPath = coverPath,
            AuthPath = authPath,
        };

        Log(ctx, $"账号：{account.DisplayName} ({account.Id})");
        Log(ctx, $"工作目录：{workspace}");
        Log(ctx, "浏览器：内置 WebView2（CDP 自动化）");
        if (project is not null)
        {
            Log(ctx, $"项目：{project.DisplayName} | 剧名={item.Title} | 集数={item.EpisodeCount}");
            Log(ctx, $"本地视频：{payload.VideoPaths.Count} 个（上传用 {payload.UploadVideoPaths.Count}）");
            Log(ctx, $"封面：{item.CoverPath ?? "(自动)"}");
        }
        else
        {
            Log(ctx, "项目：(dom 模式，未绑定队列项目)");
        }
        Log(ctx, $"授权文件：{authPath}（存在={File.Exists(authPath)}）");
        Log(ctx, $"Profile：{account.ProfileDir}");
        Log(ctx, $"模式：{opts.Mode} | 结束动作：{opts.FinalAction}");

        return (ctx, null);
    }

    private static void Log(SpikeHostContext ctx, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        File.AppendAllText(ctx.LogPath, line + Environment.NewLine);
    }
}
