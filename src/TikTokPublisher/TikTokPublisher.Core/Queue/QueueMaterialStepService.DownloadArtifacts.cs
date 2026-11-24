using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure.Automation;
using SixLabors.ImageSharp;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public static partial class QueueMaterialStepService
{
    internal static bool HasCompleteDownloadArtifacts(QueueProjectItem item)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            if (!HasRequiredDownloadMetadata(context, item))
                return false;

            var selection = ReadDownloadMetadata(context.SourceProjectDir).Episodes;
            var expected = ResolveRequiredDownloadEpisodeNumbers(context, item, selection);
            return InspectDownloadedEpisodes(context.SourceProjectDir, item, expected, allowStagedFallback: true).IsComplete;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<int> ResolveRequiredDownloadEpisodeNumbers(
        ProjectWorkspaceContext context, QueueProjectItem item, string selection)
    {
        if (XingeAutoPublishMarkerStore.TryRead(context.SourceProjectDir) is { ExpectedEpisodeTotal: > 0 } assignment)
            return Enumerable.Range(1, assignment.ExpectedEpisodeTotal).ToArray();

        if (DramaSourceRouter.ParseEpisodeSelection(selection) is { Count: > 0 } selected)
            return selected.Order().ToArray();

        if (OverLimitEpisodePolicy.TryReadKeepCount(context.SourceProjectDir, out var keep))
            return Enumerable.Range(1, keep).ToArray();

        // The provider's episode mapping describes what was requested. The number of
        // files already on disk (video_count) cannot prove that the download was complete.
        var planned = TryReadExpectedEpisodeNumbersFromDownloadState(
            context.SourceProjectDir, expectedCount: 0, matchingSelection: "all");
        if (planned is not null)
            return planned;

        var declared = ProjectWorkspaceService.ResolveDeclaredSourceEpisodeCount(context);
        var count = declared > 0 ? declared : item.SourceEpisodeCount;
        return count > 0 ? Enumerable.Range(1, count).ToArray() : [];
    }

    private static bool HasRequiredDownloadMetadata(ProjectWorkspaceContext context, QueueProjectItem item) =>
        HasOriginalDownloadPoster(context) && ResolveDownloadSynopsis(context, item).Length > 0;

    private static string ResolveDownloadSynopsis(ProjectWorkspaceContext context, QueueProjectItem item)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(context.WorkflowProjectDir, "短剧信息.txt"));
        var title = FirstNonEmpty(info.GetValueOrDefault("原剧名"), item.OriginalTitle, Path.GetFileName(context.SourceProjectDir));
        return ProjectOriginalSynopsisService.Resolve(context, title, info);
    }

    private static bool HasOriginalDownloadPoster(ProjectWorkspaceContext context)
    {
        return HasPosterIn(context.SourceProjectDir, allowLegacy: !string.Equals(
                   context.SourceProjectDir, context.WorkflowProjectDir, StringComparison.OrdinalIgnoreCase)) ||
               HasPosterIn(context.WorkflowProjectDir, allowLegacy: false);

        static bool HasPosterIn(string folder, bool allowLegacy)
        {
            if (!Directory.Exists(folder))
                return false;

            return Directory.EnumerateFiles(folder).Any(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var isOriginal = string.Equals(stem, QueueMaterialPrepareService.SourcePosterStem, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(stem, "cover", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(stem, "poster", StringComparison.OrdinalIgnoreCase) ||
                                 (allowLegacy && string.Equals(stem, QueueMaterialPrepareService.LegacySourcePosterStem, StringComparison.OrdinalIgnoreCase));
                return isOriginal && IsUsableDownloadPoster(path);
            });
        }
    }

    private static bool IsUsableDownloadPoster(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            if (stream.ReadAtLeast(header, header.Length, false) == header.Length &&
                header[4..8].SequenceEqual("ftyp"u8))
            {
                // ImageSharp does not decode HEIF. The poster workflow converts it with FFmpeg.
                var headerLength = BinaryPrimitives.ReadUInt32BigEndian(header);
                var brand = Encoding.ASCII.GetString(header[8..]);
                return headerLength >= 16 && headerLength < stream.Length &&
                       brand is "heic" or "heix" or "heim" or "heis" or "mif1";
            }

            stream.Position = 0;
            return Image.Identify(stream) is not null;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or
                                       NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static Func<string, int, string, CancellationToken, Task<UploadTitleImportService.ExactDramaLookupResult>>?
        DispatchedMaterialLookupForTests { get; set; }

    private static async Task EnsureRequiredDownloadMetadataAsync(
        ProjectWorkspaceContext context, QueueProjectItem item, DownloadMetadata metadata,
        string displayName, Action<string> log, CancellationToken ct, IDramaDownloader downloader)
    {
        var source = context.SourceProjectDir;
        var synopsis = ResolveDownloadSynopsis(context, item);
        var hasPoster = HasOriginalDownloadPoster(context);
        var router = downloader as DramaSourceRouter;

        if ((!hasPoster || synopsis.Length == 0) &&
            (router is not null || DispatchedMaterialLookupForTests is not null))
        {
            var progress = CreateDownloadProgress(log);
            if (router is not null)
            {
                try
                {
                    await router.EnsurePosterAndIntroAsync(source, displayName, progress, ct,
                        validatePoster: hasPoster ? null : IsUsableDownloadPoster,
                        downloadPoster: !hasPoster).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"使用已保存的下载资料补齐简介或海报失败：{ex.Message}");
                }
            }

            synopsis = ResolveDownloadSynopsis(context, item);
            hasPoster = HasOriginalDownloadPoster(context);
            if ((!hasPoster || synopsis.Length == 0) && !string.IsNullOrWhiteSpace(metadata.BookId))
            {
                var searchTitle = FirstNonEmpty(item.OriginalTitle, metadata.Title);
                log(IsBookIdForSource(metadata.BookId, "dramabox")
                    ? $"正在按剧名《{searchTitle}》搜索下发剧集。"
                    : "正在按原片源补齐简介或原海报。");
                var materials = new DownloadBookMaterials("", "", "");
                try
                {
                    materials = await FetchDownloadBookMaterialsAsync(metadata, item, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"按原片源补齐简介或海报失败：{ex.Message}");
                }

                if (materials.Failure.Length > 0)
                    throw new InvalidOperationException(materials.Failure);

                var title = FirstNonEmpty(item.OriginalTitle, metadata.Title, Path.GetFileName(source));
                if (synopsis.Length == 0 && ProjectOriginalSynopsisService.IsUsable(materials.Intro, title))
                {
                    synopsis = ProjectInfoTextHelper.SingleLineSynopsis(materials.Intro);
                    SaveRecoveredIntro(source, synopsis);
                }

                if (!hasPoster && router is not null &&
                    Uri.TryCreate(materials.PosterUrl, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                {
                    SaveRecoveredPosterUrl(source, materials.PosterUrl);
                    await router.EnsurePosterAndIntroAsync(source, displayName, progress, ct,
                        validatePoster: IsUsableDownloadPoster).ConfigureAwait(false);
                    hasPoster = HasOriginalDownloadPoster(context);
                }
            }
        }

        if (synopsis.Length > 0)
        {
            var introPath = Path.Combine(source, "简介.txt");
            var originalTitle = FirstNonEmpty(item.OriginalTitle, metadata.Title, Path.GetFileName(source));
            if (!ProjectOriginalSynopsisService.IsUsable(ExternalDramaInfoReader.ReadSynopsisFile(introPath), originalTitle))
            {
                WriteDownloadMetadataFile(introPath, synopsis + Environment.NewLine);
                log("已保存原简介至简介.txt。");
            }
        }

        var missing = new List<string>(2);
        if (synopsis.Length == 0) missing.Add("原简介");
        if (!hasPoster) missing.Add("原海报");
        if (missing.Count > 0)
            throw new InvalidOperationException($"下载未完成：缺少{string.Join("、", missing)}。请核对片源资料后重试下载。");
    }

    private readonly record struct DownloadBookMaterials(string Intro, string PosterUrl, string Failure);

    private static async Task<DownloadBookMaterials> FetchDownloadBookMaterialsAsync(
        DownloadMetadata metadata, QueueProjectItem item, CancellationToken ct)
    {
        var title = FirstNonEmpty(item.OriginalTitle, metadata.Title);
        if (IsBookIdForSource(metadata.BookId, "dramabox"))
        {
            if (title.Length == 0)
                return new DownloadBookMaterials("", "", "缺少原剧名，无法搜索下发剧集。");

            var episodes = Math.Max(item.SourceEpisodeCount, item.EpisodeCount);
            var lookup = DispatchedMaterialLookupForTests is { } lookupForTests
                ? await lookupForTests(title, episodes, metadata.BookId, ct).ConfigureAwait(false)
                : await UploadTitleImportService.FindExactDramaAsync(
                    title, episodes, ct, bookId: metadata.BookId).ConfigureAwait(false);
            if (lookup.Item is null)
            {
                var reason = string.IsNullOrWhiteSpace(lookup.Reason)
                    ? UploadTitleImportService.NoExactMatchReason
                    : lookup.Reason;
                return new DownloadBookMaterials("", "", reason);
            }

            return new DownloadBookMaterials(lookup.Item.Intro ?? "", lookup.Item.PosterUrl ?? "", "");
        }

        if (title.Length == 0)
            return new DownloadBookMaterials("", "", "");

        var matches = await ShortDramaDramaServices.SearchAsync(title, 1, ct).ConfigureAwait(false);
        var book = matches.FirstOrDefault(match => string.Equals(
            match.BookId, metadata.BookId, StringComparison.OrdinalIgnoreCase));
        return new DownloadBookMaterials(book?.Intro ?? "", book?.PosterUrl ?? "", "");
    }

    private static void SaveRecoveredIntro(string source, string intro)
    {
        var path = Path.Combine(source, "shortdrama-project.json");
        if (!File.Exists(path))
            return;

        var metadata = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new InvalidDataException("shortdrama-project.json 不是有效对象。");
        metadata["intro"] = intro;
        WriteDownloadMetadataFile(path, metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SaveRecoveredPosterUrl(string source, string posterUrl)
    {
        var path = Path.Combine(source, "shortdrama-project.json");
        if (!File.Exists(path))
            return;

        var metadata = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new InvalidDataException("shortdrama-project.json 不是有效对象。");
        metadata["posterUrl"] = posterUrl;
        WriteDownloadMetadataFile(path, metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteDownloadMetadataFile(string path, string content)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            ResilientFileSystem.TryDeleteFile(temporary);
        }
    }
}
