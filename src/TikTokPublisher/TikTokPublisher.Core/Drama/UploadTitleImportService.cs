using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Drama;

public sealed record UploadTitleImportRequest(string Title, int ExpectedEpisodeTotal = 0, string RawText = "");
public sealed record UploadTitleImportFailure(string Title, string Reason);
public sealed record UploadTitleImportDownloadPlan(string Episodes, int EffectiveEpisodeCount, bool Truncated);

public sealed class UploadTitleImportResult
{
    public List<string> RequestedTitles { get; } = new();
    public List<string> ProjectDirs { get; } = new();
    public List<UploadTitleImportFailure> Failures { get; } = new();
    public List<string> Duplicates { get; } = new();
    public int QueuedCount => ProjectDirs.Count;
    public int FailedCount => Failures.Count;
}

public static class UploadTitleImportService
{
    public const string MatchModeTitle = "title";
    public const string MatchModeTitleEpisode = "title_episode";
    public const int DefaultEpisodeMin = 10;
    public const int DefaultEpisodeMax = 120;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly Regex TitleEpisodePattern = new(
        @"^(?<title>.+?)[\s　]+(?<count>\d{1,5})\s*集?\s*$",
        RegexOptions.Compiled);

    public static async Task<UploadTitleImportResult> ImportAsync(
        string workspaceRoot,
        string rawText,
        ClientSettings settings,
        TikTokAccountProfile? account,
        int episodeMin = DefaultEpisodeMin,
        int episodeMax = DefaultEpisodeMax,
        string matchMode = MatchModeTitle,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var workspace = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"工作目录不存在：{workspace}");

        var (requests, parseFailures) = ParseRequests(rawText, matchMode);
        var result = new UploadTitleImportResult();
        result.RequestedTitles.AddRange(requests.Select(FormatRequestLabel));
        result.Failures.AddRange(parseFailures);

        var managementDedupEnabled = account?.ManagementDedupEnabled ?? settings.ManagementDedupEnabled;
        var managementDedupScope = string.IsNullOrWhiteSpace(account?.ManagementDedupScope)
            ? settings.ManagementDedupScope
            : account.ManagementDedupScope;
        if (managementDedupEnabled && requests.Count > 0)
        {
            var check = await TikTokManagementUploadRecordSyncService.CheckDuplicateOriginalNamesAsync(
                requests.Select(r => r.Title),
                managementDedupScope,
                account,
                ct).ConfigureAwait(false);
            if (!check.Ok)
            {
                log?.Invoke($"管理系统去重检查失败，按全部导入处理：{check.Message}");
            }
            else if (check.Duplicates.Count > 0)
            {
                foreach (var title in requests.Select(r => r.Title).Where(check.Duplicates.Contains))
                {
                    result.Duplicates.Add(title);
                    log?.Invoke($"跳过（管理系统已存在）：{title}");
                }

                requests = requests.Where(r => !check.Duplicates.Contains(r.Title)).ToList();
            }
        }

        var state = DramaDownloadQueueStore.Load();
        var authorExclude = SplitKeywords(state.AuthorExclude);
        var episodeSelection = string.IsNullOrWhiteSpace(state.DownloadEpisodeNumberMode) ? "source" : state.DownloadEpisodeNumberMode;
        var quality = string.IsNullOrWhiteSpace(state.DefaultQuality) ? settings.DramaDownloadDefaultQuality : state.DefaultQuality;
        var concurrent = state.DownloadConcurrent > 0 ? state.DownloadConcurrent : settings.DramaDownloadConcurrent;

        for (var index = 0; index < requests.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var request = requests[index];
            var label = FormatRequestLabel(request);
            log?.Invoke($"精确搜索短剧 {index + 1}/{requests.Count}：{label}");
            try
            {
                DramaSearchItem? matched = null;
                var reason = "未找到精确匹配结果";
                foreach (var query in TitleSearchQueryVariants(request.Title))
                {
                    var results = await ShortDramaDramaServices.SearchAsync(query, 1, ct).ConfigureAwait(false);
                    (matched, reason) = PickPreferredSearchMatch(request.Title, results, request.ExpectedEpisodeTotal);
                    if (matched is not null) break;
                    if (reason != "未找到精确匹配结果") break;
                }

                if (matched is null)
                {
                    result.Failures.Add(new UploadTitleImportFailure(label, reason));
                    log?.Invoke($"未加入：{label}，{reason}");
                    continue;
                }

                var authorHit = authorExclude.FirstOrDefault(token => ContainsToken(matched.Author, token));
                if (!string.IsNullOrWhiteSpace(authorHit))
                {
                    var author = string.IsNullOrWhiteSpace(matched.Author) ? authorHit : $"{matched.Author}（包含 {authorHit}）";
                    var failure = $"命中作者排除：{author}";
                    result.Failures.Add(new UploadTitleImportFailure(label, failure));
                    log?.Invoke($"未加入：{label}，{failure}");
                    continue;
                }

                var episodeError = ResolveEpisodeLimitError(matched, settings, episodeMin, episodeMax);
                if (!string.IsNullOrWhiteSpace(episodeError))
                {
                    result.Failures.Add(new UploadTitleImportFailure(label, episodeError));
                    log?.Invoke($"未加入：{label}，{episodeError}");
                    continue;
                }

                var downloadPlan = ResolveDownloadPlan(matched, settings, episodeMax);
                var projectDir = await ShortDramaDramaServices.BootstrapAsync(
                    workspace,
                    matched,
                    downloadPlan.Episodes,
                    quality,
                    concurrent,
                    episodeSelection,
                    ResolveQueueEntryDramaType(matched),
                    ct).ConfigureAwait(false);
                if (downloadPlan.Truncated)
                {
                    UpdateTruncatedProjectMetadata(projectDir, matched.EpisodeTotal, downloadPlan);
                    log?.Invoke($"超长剧已加入队列：{matched.Title} 原 {matched.EpisodeTotal} 集，仅下载前 {downloadPlan.EffectiveEpisodeCount} 集");
                }

                result.ProjectDirs.Add(projectDir);
                log?.Invoke($"已创建/更新项目：{matched.Title}（{matched.EpisodeTotal} 集）");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failures.Add(new UploadTitleImportFailure(label, ex.Message));
                log?.Invoke($"导入失败：{label}，{ex.Message}");
            }
        }

        if (result.ProjectDirs.Count > 0)
            WorkspaceQueueService.AddProjectsToQueue(workspace, result.ProjectDirs);
        return result;
    }

    public static (List<UploadTitleImportRequest> Requests, List<UploadTitleImportFailure> Failures) ParseRequests(
        string rawText,
        string matchMode)
    {
        var useEpisode = string.Equals(matchMode, MatchModeTitleEpisode, StringComparison.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var requests = new List<UploadTitleImportRequest>();
        var failures = new List<UploadTitleImportFailure>();
        foreach (var rawLine in (rawText ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!useEpisode)
            {
                if (seen.Add(rawLine))
                    requests.Add(new UploadTitleImportRequest(rawLine, 0, rawLine));
                continue;
            }

            var match = TitleEpisodePattern.Match(rawLine);
            if (!match.Success || !int.TryParse(match.Groups["count"].Value, out var count) || count <= 0)
            {
                failures.Add(new UploadTitleImportFailure(rawLine, "缺少集数"));
                continue;
            }

            var title = match.Groups["title"].Value.Trim();
            var key = $"{title}\n{count}";
            if (title.Length > 0 && seen.Add(key))
                requests.Add(new UploadTitleImportRequest(title, count, rawLine));
        }

        return (requests, failures);
    }

    public static (DramaSearchItem? Item, string Reason) PickPreferredSearchMatch(
        string title,
        IReadOnlyList<DramaSearchItem> results,
        int expectedEpisodeTotal = 0)
    {
        var target = (title ?? "").Trim();
        var exact = Dedupe(results.Where(item => string.Equals((item.Title ?? "").Trim(), target, StringComparison.Ordinal))).ToList();
        if (expectedEpisodeTotal > 0 && exact.Count > 0)
            return PickEpisodeTotalMatch(exact, expectedEpisodeTotal);
        if (exact.Count == 1) return (exact[0], "");
        if (exact.Count > 1) return (null, "命中多条同名短剧，无法唯一确认");

        var normalizedTarget = NormalizeSearchTitleText(target);
        var normalized = Dedupe(results.Where(item => NormalizeSearchTitleText(item.Title) == normalizedTarget)).ToList();
        if (expectedEpisodeTotal > 0 && normalized.Count > 0)
            return PickEpisodeTotalMatch(normalized, expectedEpisodeTotal);
        if (normalized.Count == 1) return (normalized[0], "");
        if (normalized.Count > 1) return (null, "命中多条同名短剧，无法唯一确认");

        var unique = Dedupe(results).ToList();
        if (expectedEpisodeTotal > 0 && unique.Count == 1)
            return PickEpisodeTotalMatch(unique, expectedEpisodeTotal);
        if (unique.Count == 1) return (unique[0], "");
        return (null, "未找到精确匹配结果");
    }

    private static (DramaSearchItem? Item, string Reason) PickEpisodeTotalMatch(
        IReadOnlyList<DramaSearchItem> candidates,
        int expectedEpisodeTotal)
    {
        var matched = candidates.Where(item => item.EpisodeTotal == expectedEpisodeTotal).ToList();
        if (matched.Count == 1) return (matched[0], "");
        if (matched.Count > 1)
            return (null, $"命中多条同名且均为 {expectedEpisodeTotal} 集的短剧，无法唯一确认");
        var totals = candidates.Select(item => item.EpisodeTotal).Where(v => v > 0).Distinct().Order().ToArray();
        return (null, totals.Length > 0
            ? $"找到同名短剧，但集数不匹配：输入 {expectedEpisodeTotal} 集，候选为 {string.Join("、", totals.Select(v => $"{v} 集"))}"
            : $"找到同名短剧，但候选集数缺失，无法按 {expectedEpisodeTotal} 集匹配");
    }

    public static string ResolveEpisodeLimitError(
        DramaSearchItem item,
        ClientSettings settings,
        int episodeMin = DefaultEpisodeMin,
        int episodeMax = DefaultEpisodeMax)
    {
        if (episodeMin > 0 && item.EpisodeTotal < episodeMin)
            return $"集数 {item.EpisodeTotal}，小于最小限制 {episodeMin}";
        if (episodeMax > 0 &&
            item.EpisodeTotal > episodeMax &&
            !settings.TiktokAllowOverLimitUploadImport)
            return $"集数 {item.EpisodeTotal}，大于最大限制 {episodeMax}";
        return "";
    }

    public static UploadTitleImportDownloadPlan ResolveDownloadPlan(
        DramaSearchItem item,
        ClientSettings settings,
        int episodeMax = DefaultEpisodeMax)
    {
        var max = episodeMax > 0 ? episodeMax : DefaultEpisodeMax;
        if (!settings.TiktokAllowOverLimitUploadImport || item.EpisodeTotal <= max)
            return new UploadTitleImportDownloadPlan("all", Math.Max(1, item.EpisodeTotal), Truncated: false);

        var limit = settings.TiktokOverLimitDownloadEpisodeCount <= 0
            ? max
            : settings.TiktokOverLimitDownloadEpisodeCount;
        limit = Math.Clamp(limit, 1, max);
        return new UploadTitleImportDownloadPlan($"1-{limit}", limit, Truncated: true);
    }

    private static void UpdateTruncatedProjectMetadata(
        string projectDir,
        int originalEpisodeCount,
        UploadTitleImportDownloadPlan plan)
    {
        var path = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(path))
            return;

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        root["originalEpisodeCount"] = Math.Max(originalEpisodeCount, plan.EffectiveEpisodeCount);
        root["original_episode_count"] = Math.Max(originalEpisodeCount, plan.EffectiveEpisodeCount);
        root["episodeCount"] = plan.EffectiveEpisodeCount;
        root["episode_count"] = plan.EffectiveEpisodeCount;
        root["effectiveEpisodeCount"] = plan.EffectiveEpisodeCount;
        root["effective_episode_count"] = plan.EffectiveEpisodeCount;
        root["downloadEpisodeLimit"] = plan.EffectiveEpisodeCount;
        root["download_episode_limit"] = plan.EffectiveEpisodeCount;
        root["episodes"] = plan.Episodes;
        root["truncatedForTikTokUpload"] = true;
        root["truncated_for_tiktok_upload"] = true;
        File.WriteAllText(path, root.ToJsonString(MetadataJsonOptions));
    }

    private static IEnumerable<DramaSearchItem> Dedupe(IEnumerable<DramaSearchItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = string.IsNullOrWhiteSpace(item.BookId) ? NormalizeSearchTitleText(item.Title) : item.BookId.Trim();
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return item;
        }
    }

    private static IReadOnlyList<string> TitleSearchQueryVariants(string title)
    {
        var baseText = (title ?? "").Trim();
        var variants = new[]
            {
                baseText,
                TranslatePunctuation(baseText, halfToFull: true),
                TranslatePunctuation(baseText, halfToFull: false),
                NormalizeSearchTitleText(baseText),
            }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return variants;
    }

    private static string TranslatePunctuation(string text, bool halfToFull)
    {
        var map = halfToFull
            ? new Dictionary<char, char> { [':'] = '：', [','] = '，', ['!'] = '！', ['?'] = '？', [';'] = '；', ['('] = '（', [')'] = '）' }
            : new Dictionary<char, char> { ['：'] = ':', ['，'] = ',', ['！'] = '!', ['？'] = '?', ['；'] = ';', ['（'] = '(', ['）'] = ')' };
        return string.Concat((text ?? "").Select(ch => map.TryGetValue(ch, out var replacement) ? replacement : ch));
    }

    private static string NormalizeSearchTitleText(string? title) =>
        Regex.Replace((title ?? "").Trim().ToLowerInvariant(), @"[\s　《》<>“”""'：:，,。.!！?？、\-_【】\[\]()（）]+", "");

    private static string FormatRequestLabel(UploadTitleImportRequest request) =>
        request.ExpectedEpisodeTotal > 0 ? $"{request.Title}（{request.ExpectedEpisodeTotal}集）" : request.Title;

    private static IReadOnlyList<string> SplitKeywords(string? value) =>
        (value ?? "").Split(['\r', '\n', ',', '，', ';', '；', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsToken(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.IsNullOrWhiteSpace(token) &&
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string ResolveQueueEntryDramaType(DramaSearchItem item)
    {
        var source = (item.SourceMode ?? "").Trim().ToLowerInvariant();
        if (source == "mj_today") return "mj";
        if (source == "aiju_today") return "aiju";
        if (ContainsToken(item.Category, "漫剧")) return "mj";
        if (ContainsToken(item.Category, "AI") || ContainsToken(item.Title, "AI")) return "aiju";
        return "";
    }
}
