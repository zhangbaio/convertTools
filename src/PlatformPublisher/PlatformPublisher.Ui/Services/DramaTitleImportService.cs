using System.Text.Json;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Services;

public sealed record DramaTitleImportOutcome(
    IReadOnlyList<string> ProjectDirectories,
    IReadOnlyList<UploadTitleImportFailure> Failures,
    int RequestedCount)
{
    public IReadOnlyList<string> Duplicates { get; init; } = [];
    public bool Cancelled { get; init; }
}

public sealed class DramaTitleImportService(IDramaSearchService search, YunfanDramaSettingsProvider settingsProvider)
{
    public const int MinimumEpisodes = 10;
    public const int MaximumEpisodes = 200;

    public Task<DramaTitleImportOutcome> ImportAsync(
        string workspaceDirectory, string titlesText, Action<string>? log, CancellationToken cancellationToken,
        int expectedEpisodes = 0) =>
        ImportCoreAsync(workspaceDirectory, titlesText, UploadTitleImportService.MatchModeTitle,
            settingsProvider.LoadClientSettings(), expectedEpisodes, log, cancellationToken, existing: null);

    public Task<DramaTitleImportOutcome> ImportForWeixinAsync(
        string workspaceDirectory, string titlesText, string matchMode, ClientSettings settings,
        IReadOnlySet<string> accountProjectDirectories, IReadOnlySet<string> otherAccountDirectories,
        Action<string>? log, CancellationToken cancellationToken) =>
        ImportCoreAsync(workspaceDirectory, titlesText, matchMode, settings, 0, log, cancellationToken,
            new ExistingProjects(accountProjectDirectories, otherAccountDirectories));

    private async Task<DramaTitleImportOutcome> ImportCoreAsync(
        string workspaceDirectory, string titlesText, string matchMode, ClientSettings settings, int expectedEpisodes,
        Action<string>? log, CancellationToken ct, ExistingProjects? existing)
    {
        var workspace = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"工作目录不存在：{workspace}");

        var duplicates = new List<string>();
        var (requests, failures) = UploadTitleImportService.ParseRequests(titlesText, matchMode,
            existing is null ? null : duplicates);
        if (requests.Count == 0 && failures.Count == 0)
            throw new InvalidOperationException("请输入至少一个短剧名称。");

        var requestedCount = requests.Count + failures.Count + duplicates.Count;
        var projectDirectories = new List<string>();
        var cancelled = false;
        try
        {
            var existingIds = existing is null ? null : await Task.Run(
                () => ReadExistingBookIds(existing.AccountDirectories, ct), ct);
            var sourceSettings = existing is null ? null : DramaSourceSettingsMapping.FromClientSettings(settings);
            for (var index = 0; index < requests.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var request = requests[index];
                log?.Invoke($"精确搜索短剧 {index + 1}/{requests.Count}：{request.Title}");
                try
                {
                    var searchResults = sourceSettings is not null && search is DramaSourceRouter router
                        ? await router.SearchWithSettingsAsync(sourceSettings, request.Title, 1, ct)
                        : await search.SearchAsync(request.Title, 1, ct);
                    var items = searchResults.Select(ShortDramaDramaServices.FromCore).ToArray();
                    var expected = existing is null ? expectedEpisodes : request.ExpectedEpisodeTotal;
                    var (match, reason) = existing is null && expected > 0
                        ? StrictMatch(request.Title, expected, items)
                        : UploadTitleImportService.PickPreferredSearchMatch(request.Title,
                            existing is null ? items : items.Where(item => !string.IsNullOrWhiteSpace(item.BookId)).ToArray(),
                            expected, allowSingleFallback: existing is null);
                    if (match is null)
                    {
                        Fail(request.Title, reason);
                        continue;
                    }

                    var episodeError = GetEpisodeLimitError(match.EpisodeTotal);
                    if (episodeError.Length > 0)
                    {
                        Fail(request.Title, episodeError);
                        continue;
                    }

                    var bookId = match.BookId?.Trim() ?? "";
                    if (existingIds?.Contains(bookId) == true)
                    {
                        SkipDuplicate(request.Title);
                        continue;
                    }

                    var directory = await ShortDramaDramaServices.BootstrapAsync(workspace, match, "all",
                        settings.DramaDownloadDefaultQuality, settings.DramaDownloadConcurrent, "source", "short", ct,
                        beforeWrite: existing is null ? null : ProtectExistingDirectory,
                        expectedEpisodeTotal: expected);
                    projectDirectories.Add(directory);
                    existingIds?.Add(bookId);
                    log?.Invoke($"已准备入队：{match.Title}（{match.EpisodeTotal} 集）");
                }
                catch (QueuedDramaException)
                {
                    SkipDuplicate(request.Title);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Fail(request.Title, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (existing is not null && ct.IsCancellationRequested)
        {
            cancelled = true;
        }

        return new DramaTitleImportOutcome(projectDirectories, failures, requestedCount)
        {
            Duplicates = duplicates,
            Cancelled = cancelled,
        };

        void ProtectExistingDirectory(string directory)
        {
            ct.ThrowIfCancellationRequested();
            if (existing!.AccountDirectories.Contains(directory))
                throw new QueuedDramaException();
            if (existing.OtherAccountDirectories.Contains(directory))
                throw new InvalidOperationException("项目目录已被其他账号使用。");
        }

        void SkipDuplicate(string title)
        {
            duplicates.Add(title);
            log?.Invoke($"已跳过重复剧集：{title}");
        }

        void Fail(string title, string reason)
        {
            failures.Add(new UploadTitleImportFailure(title, reason));
            log?.Invoke($"未加入：{title}，{reason}");
        }
    }

    private static string GetEpisodeLimitError(int episodes) => episodes switch
    {
        < MinimumEpisodes => $"集数 {episodes}，小于最小限制 {MinimumEpisodes}",
        > MaximumEpisodes => $"集数 {episodes}，大于最大限制 {MaximumEpisodes}",
        _ => "",
    };

    private static HashSet<string> ReadExistingBookIds(IReadOnlySet<string> directories, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var metadata = Path.Combine(directory, "shortdrama-project.json");
                if (!File.Exists(metadata))
                    metadata = Path.Combine(directory, "source", "shortdrama-project.json");
                using var document = JsonDocument.Parse(File.ReadAllText(metadata));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("bookId", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } bookId)
                    ids.Add(bookId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 缺少历史来源信息的项目仍按队列目录去重。
            }
        }
        return ids;
    }

    private static (DramaSearchItem? Item, string Reason) StrictMatch(string title, int episodes, IReadOnlyList<DramaSearchItem> items)
    {
        var match = UploadTitleImportService.PickStrictDispatchMatch(title, episodes, items);
        return (match.Item, match.Reason);
    }

    private sealed record ExistingProjects(IReadOnlySet<string> AccountDirectories, IReadOnlySet<string> OtherAccountDirectories);
    private sealed class QueuedDramaException : Exception { }
}
