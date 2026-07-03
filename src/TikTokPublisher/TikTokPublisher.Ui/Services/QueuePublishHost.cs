using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services;

public sealed class QueuePublishHost : IQueuePublishHost
{
    private readonly Func<TikTokAccountProfile, CancellationToken, Task<bool>> _ensureBrowser;
    private readonly Func<TikTokAccountProfile, QueueProjectItem, FinalAction, Action<string>, CancellationToken, Task<PublishResult>> _publish;

    public QueuePublishHost(
        Func<TikTokAccountProfile, CancellationToken, Task<bool>> ensureBrowser,
        Func<TikTokAccountProfile, QueueProjectItem, FinalAction, Action<string>, CancellationToken, Task<PublishResult>> publish)
    {
        _ensureBrowser = ensureBrowser;
        _publish = publish;
    }

    public Task<bool> EnsureAccountBrowserReadyAsync(TikTokAccountProfile account, CancellationToken ct) =>
        _ensureBrowser(account, ct);

    public Task<PublishResult> PublishProjectAsync(
        TikTokAccountProfile account,
        QueueProjectItem project,
        FinalAction finalAction,
        Action<string> log,
        CancellationToken ct) =>
        _publish(account, project, finalAction, log, ct);

    public static PublishItem ToPublishItem(QueueProjectItem project)
    {
        var uploadVideos = !string.IsNullOrWhiteSpace(project.ProjectDir)
            ? ProjectVideoResolver.ResolveUploadVideos(project.ProjectDir, allowStagedFallback: true).ToList()
            : new List<string>();
        if (uploadVideos.Count == 0 && !string.IsNullOrWhiteSpace(project.PrimaryVideoPath))
            uploadVideos.Add(project.PrimaryVideoPath);

        return new PublishItem
        {
            VideoPath = uploadVideos.Count > 0 ? uploadVideos[0] : "",
            Title = project.Title,
            OriginalTitle = project.OriginalTitle,
            DramaName = project.Title,
            Description = project.Description,
            GenreCategory = project.GenreCategory,
            EpisodeCount = !string.IsNullOrWhiteSpace(project.ProjectDir)
                ? ProjectWorkspaceService.ResolveSourceEpisodeCount(project.ProjectDir)
                : Math.Max(project.EpisodeCount, Math.Max(1, uploadVideos.Count)),
            CoverPath = project.CoverPath,
            ProjectKey = project.DisplayName,
            ProjectDir = project.ProjectDir,
        };
    }
}
