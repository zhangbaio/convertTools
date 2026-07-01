using Microsoft.Playwright;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.AI;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation.Weixin.Pages;

public sealed class WeixinMaterialPublishPage
{
    private const string PublishVideoSourceModeProject = "project";
    private const string PublishVideoSourceModeMaterialClips = "material_clips";
    private static readonly Regex EpisodeIndexRegex = new(
        @"第\s*0*(\d+)\s*集|episode\s*0*(\d+)|ep\s*0*(\d+)|(^|[^\d])0*(\d+)(?=[^\d]*$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record PublishVideoItem(int EpisodeIndex, string VideoPath);

    public async Task NavigateAsync(
        IPage page,
        WeixinNavigationOptions navigation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await TryClickEntryAsync(page, navigation.EntryButton))
        {
            return;
        }

        var steps = new[]
        {
            navigation.Section,
            navigation.Item,
            "内容管理",
            "视频",
            "全部视频",
            navigation.Item
        };

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step))
            {
                continue;
            }

            if (await MaybeClickTextAsync(page, step, 4_000))
            {
                await WaitBrieflyForLoadAsync(page);
                if (await TryClickEntryAsync(page, navigation.EntryButton))
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException($"未找到发表视频入口: {navigation.Section} -> {navigation.Item} -> {navigation.EntryButton}");
    }

    public async Task WaitForReadyAsync(
        IPage page,
        WeixinVideoPublishOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.GetByText(options.ReadyText, new PageGetByTextOptions
        {
            Exact = false
        }).First.WaitForAsync(new LocatorWaitForOptions
        {
            Timeout = 20_000
        });
    }

    public async Task UploadVideosAsync(
        IPage page,
        IReadOnlyList<string> videoPaths,
        WeixinVideoPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = page.Locator(options.VideoUploadSelector).First;
        await input.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 10_000
        });

        await input.SetInputFilesAsync(videoPaths.ToArray());
        progress?.Report($"微信素材上传：已选择 {videoPaths.Count} 个视频文件。");
        if (options.WaitAfterUploadSeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.WaitAfterUploadSeconds), cancellationToken);
        }
    }

    public async Task FillDescriptionAsync(
        IPage page,
        string description,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = await FindEditableFieldAsync(
            page,
            "视频描述",
            "textarea[placeholder*='添加描述'], textarea, [contenteditable='true']",
            10_000);
        await FillLocatorAsync(field, description);
        progress?.Report("微信素材上传：已填写视频描述。");
    }

    public async Task ChooseOptionsAsync(
        IPage page,
        WeixinVideoPublishOptions options,
        string seriesTitle,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(options.LocationOptionText))
        {
            await ChooseOptionAsync(page, "位置", options.LocationOptionText);
            progress?.Report($"微信素材上传：已选择位置 -> {options.LocationOptionText}");
        }

        if (!string.IsNullOrWhiteSpace(options.LinkOptionText))
        {
            await ChooseOptionAsync(page, "链接", options.LinkOptionText);
            await OpenSeriesPickerAsync(page, options);
            await SearchAndSelectSeriesAsync(page, options, seriesTitle);
            progress?.Report($"微信素材上传：已关联剧集 -> {seriesTitle}");
        }

        if (!string.IsNullOrWhiteSpace(options.ActivityOptionText))
        {
            await ChooseOptionAsync(page, "活动", options.ActivityOptionText);
            progress?.Report($"微信素材上传：已选择活动 -> {options.ActivityOptionText}");
        }

        if (!string.IsNullOrWhiteSpace(options.TimingOptionText))
        {
            await ChooseOptionAsync(page, "定时发表", options.TimingOptionText);
            progress?.Report($"微信素材上传：已选择定时发表 -> {options.TimingOptionText}");
        }
    }

    public async Task FillShortTitleAsync(
        IPage page,
        string shortTitle,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = await FindEditableFieldAsync(page, "短标题", "input[placeholder*='短标题'], input", 10_000);
        await field.FillAsync(shortTitle);
        progress?.Report($"微信素材上传：已填写短标题 -> {shortTitle}");
    }

    public async Task FinalizeAsync(
        IPage page,
        WeixinVideoPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var button = await FindVisibleTextAsync(page, options.FinalActionText, 10_000);
        await button.ClickAsync();
        progress?.Report($"微信素材上传：已点击 {options.FinalActionText}");

        if (options.WaitAfterFinalActionSeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.WaitAfterFinalActionSeconds), cancellationToken);
        }
    }

    public async Task SaveArtifactsAsync(
        IPage page,
        WeixinAutomationConfig config,
        string outputDirectory,
        string stem,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputDirectory);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(outputDirectory, $"{stem}.png"),
            FullPage = true
        });

        if (config.Debug.SaveHtml)
        {
            var html = await page.ContentAsync();
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"{stem}.html"), html, cancellationToken);
        }

        if (config.Debug.SaveText)
        {
            var text = await page.Locator("body").InnerTextAsync();
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"{stem}.txt"), text, cancellationToken);
        }
    }

    private static async Task<bool> TryClickEntryAsync(IPage page, string entryText)
    {
        if (await MaybeClickTextAsync(page, entryText, 3_000))
        {
            await WaitBrieflyForLoadAsync(page);
            return true;
        }

        return false;
    }

    private static async Task<bool> MaybeClickTextAsync(IPage page, string text, int timeoutMs)
    {
        try
        {
            var target = await FindVisibleTextAsync(page, text, timeoutMs);
            await target.ClickAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ILocator> FindVisibleTextAsync(IPage page, string text, int timeoutMs)
    {
        var candidates = new[]
        {
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = text, Exact = false }).First,
            page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameString = text, Exact = false }).First,
            page.GetByText(text, new PageGetByTextOptions { Exact = false }).First
        };

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs
                });
                return candidate;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException($"未找到文本: {text}");
    }

    private static async Task WaitBrieflyForLoadAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = 5_000
            });
        }
        catch
        {
        }

        await Task.Delay(500);
    }

    private static async Task ChooseOptionAsync(IPage page, string fieldLabel, string optionText)
    {
        var group = await FindGroupByLabelAsync(page, fieldLabel, 10_000);
        try
        {
            await group.ClickAsync();
        }
        catch
        {
        }

        var option = await FindVisibleTextAsync(page, optionText, 10_000);
        await option.ClickAsync();
        await WaitBrieflyForLoadAsync(page);
    }

    private static async Task OpenSeriesPickerAsync(IPage page, WeixinVideoPublishOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.LinkPickerSelector))
        {
            var selector = page.Locator(options.LinkPickerSelector).First;
            try
            {
                await selector.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 3_000
                });
                await selector.ClickAsync();
                return;
            }
            catch
            {
            }
        }

        var button = await FindVisibleTextAsync(page, options.LinkPickerButtonText, 5_000);
        await button.ClickAsync();
    }

    private static async Task SearchAndSelectSeriesAsync(IPage page, WeixinVideoPublishOptions options, string seriesTitle)
    {
        var dialog = await FindVisibleTextAsync(page, options.LinkDialogTitle, 10_000);
        var root = dialog.Locator("xpath=ancestor-or-self::*[contains(@class,'dialog') or contains(@class,'popup') or contains(@class,'modal')][1]");
        var searchBox = root.Locator($"input[placeholder*='{Escape(options.LinkSearchPlaceholder)}'], input").First;
        try
        {
            await searchBox.FillAsync(seriesTitle);
        }
        catch
        {
            var globalSearch = page.Locator($"input[placeholder*='{Escape(options.LinkSearchPlaceholder)}'], input").First;
            await globalSearch.FillAsync(seriesTitle);
        }

        await Task.Delay(500);
        var result = await FindVisibleTextAsync(page, seriesTitle, 10_000);
        await result.ClickAsync();
        var confirm = await FindVisibleTextAsync(page, "确定", 5_000);
        await confirm.ClickAsync();
        await WaitBrieflyForLoadAsync(page);
    }

    private static async Task<ILocator> FindEditableFieldAsync(IPage page, string label, string fallbackSelector, int timeoutMs)
    {
        try
        {
            var group = await FindGroupByLabelAsync(page, label, timeoutMs);
            var field = group.Locator("textarea, input, [contenteditable='true']").First;
            await field.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs
            });
            return field;
        }
        catch
        {
            var fallback = page.Locator(fallbackSelector).First;
            await fallback.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs
            });
            return fallback;
        }
    }

    private static async Task<ILocator> FindGroupByLabelAsync(IPage page, string label, int timeoutMs)
    {
        var safe = Escape(label);
        var candidates = new[]
        {
            page.Locator($".weui-desktop-form__control-group:has-text(\"{safe}\")").First,
            page.Locator($".weui-desktop-form__label:has-text(\"{safe}\")").First,
            page.GetByLabel(label, new PageGetByLabelOptions { Exact = false }).First
        };

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs
                });
                return candidate;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException($"未找到字段: {label}");
    }

    private static async Task FillLocatorAsync(ILocator locator, string value)
    {
        try
        {
            await locator.FillAsync(value);
        }
        catch
        {
            await locator.ClickAsync();
            await locator.PressAsync("Meta+A");
            await locator.PressSequentiallyAsync(value);
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    public static IReadOnlyList<string> ResolvePublishVideoPaths(string projectDir, WeixinVideoPublishOptions options)
    {
        return ResolvePublishVideoItems(projectDir, options)
            .Select(item => item.VideoPath)
            .ToList();
    }

    public static IReadOnlyList<PublishVideoItem> ResolvePublishVideoItems(string projectDir, WeixinVideoPublishOptions options)
    {
        if (string.Equals(NormalizeVideoSourceMode(options.VideoSourceMode), PublishVideoSourceModeMaterialClips, StringComparison.Ordinal))
        {
            var clipFiles = ResolveMaterialClipVideoFiles(projectDir);
            if (clipFiles.Count == 0)
            {
                return [];
            }

            var stableKeys = ResolveStableMaterialClipKeys(clipFiles);
            return stableKeys
                .Select((key, index) => new PublishVideoItem(key, clipFiles[index]))
                .ToList();
        }

        var materialVideosDir = Path.Combine(projectDir, "material-videos");
        var videosDir = Path.Combine(projectDir, "videos");
        var materialVideoCount = Directory.Exists(materialVideosDir)
            ? Directory.EnumerateFiles(materialVideosDir, "*.*", SearchOption.TopDirectoryOnly).Count(IsVideoFile)
            : 0;
        var videoCount = Directory.Exists(videosDir)
            ? Directory.EnumerateFiles(videosDir, "*.*", SearchOption.TopDirectoryOnly).Count(IsVideoFile)
            : 0;

        var baseDir = materialVideoCount > 0 &&
                      (videoCount == 0 || materialVideoCount >= videoCount)
            ? materialVideosDir
            : videoCount > 0
                ? videosDir
                : projectDir;
        var files = Directory.EnumerateFiles(baseDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsVideoFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            return [];
        }

        var selectedIndexes = ResolveEpisodeIndexes(options, files.Count);
        return selectedIndexes
            .Where(index => index >= 1 && index <= files.Count)
            .Select(index => new PublishVideoItem(index, files[index - 1]))
            .GroupBy(item => item.VideoPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".avi", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<int> ResolveEpisodeIndexes(WeixinVideoPublishOptions options, int fileCount)
    {
        if (string.Equals(options.EpisodeSelectionMode, "explicit", StringComparison.OrdinalIgnoreCase) &&
            options.EpisodeIndexes.Count > 0)
        {
            return options.EpisodeIndexes;
        }

        var start = Math.Max(1, options.StartEpisodeIndex);
        var count = Math.Max(1, options.PublishCount);
        var results = new List<int>();
        for (var index = start; index < start + count && index <= fileCount; index++)
        {
            results.Add(index);
        }

        return results;
    }

    internal static string NormalizeVideoSourceMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "material_clips" or "material_clip" or "material_highlights" or "highlight_clips" or "clip_highlights" => PublishVideoSourceModeMaterialClips,
            _ => PublishVideoSourceModeProject
        };
    }

    internal static IReadOnlyList<string> ResolveMaterialClipVideoFiles(string projectDir)
    {
        var projectPath = Path.GetFullPath(projectDir);
        var candidates = new List<string>();
        AddCandidate(candidates, Path.Combine(projectPath, "material-clip-output"));
        AddCandidate(candidates, Path.Combine(projectPath, "material-clip-output", "renders", "clips"));

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(candidate, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(IsVideoFile)
                         .OrderBy(BuildNaturalSortToken, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    results.Add(fullPath);
                }
            }
        }

        return results;
    }

    internal static IReadOnlyList<int> ResolveStableMaterialClipKeys(IReadOnlyList<string> clipFiles)
    {
        if (clipFiles.Count == 0)
        {
            return [];
        }

        var extracted = clipFiles.Select(TryExtractEpisodeIndex).ToArray();
        if (extracted.All(value => value.HasValue) &&
            extracted.Select(value => value!.Value).Distinct().Count() == clipFiles.Count)
        {
            return extracted.Select(value => value!.Value).ToArray();
        }

        return Enumerable.Range(1, clipFiles.Count).ToArray();
    }

    internal static int? TryExtractEpisodeIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = EpisodeIndexRegex.Match(name);
        if (!match.Success)
        {
            return null;
        }

        foreach (var group in match.Groups.Cast<Group>().Skip(1))
        {
            if (group.Success && int.TryParse(group.Value, out var episodeIndex) && episodeIndex > 0)
            {
                return episodeIndex;
            }
        }

        return null;
    }

    private static string BuildNaturalSortToken(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Path.GetFileName(path);
        }

        var parts = Regex.Split(name, @"(\d+)");
        var keys = new List<string>();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (int.TryParse(part, out var number))
            {
                keys.Add(number.ToString("D8"));
            }
            else
            {
                keys.Add(part.ToLowerInvariant());
            }
        }

        keys.Add(Path.GetFileName(path).ToLowerInvariant());
        return string.Join("|", keys);
    }

    private static void AddCandidate(ICollection<string> targets, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!targets.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            targets.Add(fullPath);
        }
    }

    public static string BuildPublishDescription(ProjectInfo projectInfo, WeixinVideoPublishOptions options)
    {
        var description = !string.IsNullOrWhiteSpace(projectInfo.Tags)
            ? projectInfo.Tags.Trim()
            : (string.IsNullOrWhiteSpace(options.DescriptionTemplate)
                ? "{新剧名}"
                : options.DescriptionTemplate)
                .Replace("{新剧名}", projectInfo.Title, StringComparison.Ordinal)
                .Replace("{原剧名}", projectInfo.OriginalTitle, StringComparison.Ordinal);

        if (options.PrependHashToDescription &&
            !string.IsNullOrWhiteSpace(description) &&
            !description.TrimStart().StartsWith('#'))
        {
            description = "#" + description.TrimStart();
        }

        return description;
    }

    public static string BuildShortTitle(ProjectInfo projectInfo, WeixinVideoPublishOptions options)
    {
        if (!string.IsNullOrWhiteSpace(projectInfo.ShortTitle))
        {
            var explicitTitle = ProjectInfoTextNormalizer.SanitizeShortTitle(projectInfo.ShortTitle, options.ShortTitleMaxLength);
            if (!string.IsNullOrWhiteSpace(explicitTitle))
            {
                return explicitTitle;
            }
        }

        var source = string.IsNullOrWhiteSpace(projectInfo.Title)
            ? projectInfo.OriginalTitle
            : projectInfo.Title;
        return ProjectInfoTextNormalizer.SanitizeShortTitle(source, options.ShortTitleMaxLength);
    }
}
