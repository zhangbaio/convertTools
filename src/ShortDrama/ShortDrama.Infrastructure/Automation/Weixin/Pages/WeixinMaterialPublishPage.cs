using Microsoft.Playwright;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.AI;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation.Weixin.Pages;

public sealed class WeixinMaterialPublishPage
{
    private const string PublishVideoSourceModeProject = "project";
    private const string PublishVideoSourceModeMaterialClips = "material_clips";
    private const string PublishVideoSourceModeCustomFiles = "custom_files";
    private const string PublishVideoSourceModeDownloadedSystemHighlight = "downloaded_system_highlight";
    private const string PublishVideoSourceModeMaterialVideoDownload = "material_video_download";
    private const string PublishVideoSourceModeDirectoryPublish = "directory_publish";
    private const string PublishVideoSourceModeProjectMaterials = "project_materials";
    private const string PublishVideoSourceModeSourceVideos = "source_videos";
    private static readonly string[] DirectoryPublishDescriptionFileNames =
    [
        "description.txt", "desc.txt", "描述.txt"
    ];
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

        if (options.DeclareOriginal)
        {
            await SetCheckedByLabelAsync(page, "声明原创", enabled: true, cancellationToken);
            await HandleOriginalDeclarationDialogAsync(page, cancellationToken);
            progress?.Report("微信素材上传：已勾选声明原创。");
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

    private static async Task SetCheckedByLabelAsync(
        IPage page,
        string label,
        bool enabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var safe = Escape(label);
        var group = await FirstVisibleOrNullAsync(
            [
                page.Locator($".form-item:has(.label:has-text(\"{safe}\"))").First,
                page.Locator($".weui-desktop-form__control-group:has(.weui-desktop-form__label:has-text(\"{safe}\"))").First,
                page.Locator($"label:has-text(\"{safe}\")").First
            ],
            2_000);
        var input = await FirstAttachedOrNullAsync(
            [
                group?.Locator("input[type='checkbox'], input[type='radio']").First,
                page.GetByLabel(label, new PageGetByLabelOptions { Exact = false }).First,
                page.Locator($"label:has-text(\"{safe}\") input[type='checkbox'], label:has-text(\"{safe}\") input[type='radio']").First
            ],
            1_000);

        if (input is not null)
        {
            var current = await ReadCheckableStateAsync(input);
            if (current == enabled)
            {
                return;
            }

            try
            {
                if (enabled)
                {
                    await input.CheckAsync(new LocatorCheckOptions { Force = true, Timeout = 1_000 });
                }
                else
                {
                    await input.UncheckAsync(new LocatorUncheckOptions { Force = true, Timeout = 1_000 });
                }
            }
            catch
            {
                await input.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 1_000 });
            }

            await Task.Delay(250, cancellationToken);
            current = await ReadCheckableStateAsync(input);
            if (current == enabled || current is null)
            {
                return;
            }
        }

        var checkable = await FirstVisibleOrNullAsync(
            [
                group?.Locator(".weui-desktop-icon-checkbox, .weui-desktop-icon-radio, [role='checkbox'], [aria-checked]").First,
                page.Locator($"[role='checkbox']:has-text(\"{safe}\")").First,
                page.GetByText(label, new PageGetByTextOptions { Exact = false }).First
            ],
            3_000);
        if (checkable is null)
        {
            return;
        }

        var state = await ReadCheckableStateAsync(checkable);
        if (state != enabled)
        {
            await checkable.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 2_000 });
            await Task.Delay(250, cancellationToken);
        }
    }

    private static async Task HandleOriginalDeclarationDialogAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var existingPages = page.Context.Pages.ToArray();
        var dialog = await FirstVisibleOrNullAsync(
            [
                page.Locator(".weui-desktop-dialog__wrp:has-text(\"原创权益\"), .weui-desktop-dialog:has-text(\"原创权益\"), .dialog:has-text(\"原创权益\")").First,
                page.GetByText("原创权益", new PageGetByTextOptions { Exact = false })
                    .Locator("xpath=ancestor::*[contains(@class,'dialog') or contains(@class,'Dialog')][1]")
                    .First
            ],
            4_000);
        if (dialog is null)
        {
            return;
        }

        var agreementRow = await FirstVisibleOrNullAsync(
            [
                dialog.Locator("label:has-text(\"我已阅读并同意\")").First,
                dialog.GetByText("我已阅读并同意", new LocatorGetByTextOptions { Exact = false })
                    .Locator("xpath=ancestor::*[self::label or self::div][1]")
                    .First,
                dialog.Locator("label").Filter(new LocatorFilterOptions
                {
                    Has = dialog.GetByText("我已阅读并同意", new LocatorGetByTextOptions { Exact = false })
                }).First
            ],
            2_000);
        if (agreementRow is not null)
        {
            await SetDialogCheckableAsync(agreementRow, enabled: true, cancellationToken);
        }

        var confirm = await FirstVisibleOrNullAsync(
            [
                dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "声明原创", Exact = false }).First,
                dialog.Locator("button:has-text(\"声明原创\")").First,
                dialog.GetByText("声明原创", new LocatorGetByTextOptions { Exact = false }).First
            ],
            4_000);
        if (confirm is not null)
        {
            await confirm.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 2_000 });
        }

        foreach (var candidate in page.Context.Pages)
        {
            if (ReferenceEquals(candidate, page) || existingPages.Contains(candidate))
            {
                continue;
            }

            try
            {
                if (candidate.Url.Contains("weixin_agreement", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Url.Contains("readtemplate", StringComparison.OrdinalIgnoreCase))
                {
                    await candidate.CloseAsync();
                }
            }
            catch
            {
            }
        }

        try
        {
            await dialog.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = 3_000
            });
        }
        catch
        {
        }
    }

    private static async Task SetDialogCheckableAsync(
        ILocator row,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var input = await FirstAttachedOrNullAsync(
            [
                row.Locator("input[type='checkbox'], input[type='radio']").First,
                row.Locator("[role='checkbox'], [aria-checked]").First
            ],
            1_000);
        if (input is not null)
        {
            var current = await ReadCheckableStateAsync(input);
            if (current == enabled)
            {
                return;
            }

            try
            {
                if (enabled)
                {
                    await input.CheckAsync(new LocatorCheckOptions { Force = true, Timeout = 1_000 });
                }
                else
                {
                    await input.UncheckAsync(new LocatorUncheckOptions { Force = true, Timeout = 1_000 });
                }
            }
            catch
            {
                await input.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 1_000 });
            }

            await Task.Delay(250, cancellationToken);
            return;
        }

        await row.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 1_000 });
        await Task.Delay(250, cancellationToken);
    }

    private static async Task<bool?> ReadCheckableStateAsync(ILocator locator)
    {
        try
        {
            if (await locator.CountAsync() <= 0)
            {
                return null;
            }

            return await locator.EvaluateAsync<bool?>(
                """
                element => {
                  const input = element.matches?.("input[type='checkbox'], input[type='radio']")
                    ? element
                    : element.querySelector?.("input[type='checkbox'], input[type='radio']");
                  if (input) return !!input.checked;
                  const role = element.matches?.("[role='checkbox'], [aria-checked]")
                    ? element
                    : element.querySelector?.("[role='checkbox'], [aria-checked]");
                  const checked = role?.getAttribute?.("aria-checked");
                  if (checked === "true") return true;
                  if (checked === "false") return false;
                  return null;
                }
                """);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> FirstVisibleOrNullAsync(IEnumerable<ILocator?> candidates, int timeoutMs)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs
                });
                return candidate;
            }
            catch
            {
            }
        }

        return null;
    }

    private static async Task<ILocator?> FirstAttachedOrNullAsync(IEnumerable<ILocator?> candidates, int timeoutMs)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = timeoutMs
                });
                return candidate;
            }
            catch
            {
            }
        }

        return null;
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
        var sourceMode = NormalizeVideoSourceMode(options.VideoSourceMode);
        if (string.Equals(sourceMode, PublishVideoSourceModeMaterialClips, StringComparison.Ordinal))
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

        if (string.Equals(sourceMode, PublishVideoSourceModeCustomFiles, StringComparison.Ordinal))
        {
            return BuildPublishItemsFromFiles(options.CustomVideoFiles, options);
        }

        if (string.Equals(sourceMode, PublishVideoSourceModeDirectoryPublish, StringComparison.Ordinal))
        {
            return BuildPublishItemsFromFiles(
                ResolveDirectoryPublishVideoFiles(projectDir),
                options,
                preserveOrder: true);
        }

        if (string.Equals(sourceMode, PublishVideoSourceModeDownloadedSystemHighlight, StringComparison.Ordinal) ||
            string.Equals(sourceMode, PublishVideoSourceModeMaterialVideoDownload, StringComparison.Ordinal) ||
            string.Equals(sourceMode, PublishVideoSourceModeSourceVideos, StringComparison.Ordinal))
        {
            var sourceFiles = Directory.Exists(projectDir)
                ? Directory.EnumerateFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(IsVideoFile)
                    .OrderBy(BuildNaturalSortToken, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            return BuildPublishItemsFromFiles(sourceFiles, options);
        }

        var materialVideosDir = Path.Combine(projectDir, "material-videos");
        var videosDir = Path.Combine(projectDir, "videos");
        var preferProjectMaterials = string.Equals(sourceMode, PublishVideoSourceModeProjectMaterials, StringComparison.Ordinal);
        var materialVideoCount = Directory.Exists(materialVideosDir)
            ? Directory.EnumerateFiles(materialVideosDir, "*.*", SearchOption.TopDirectoryOnly).Count(IsVideoFile)
            : 0;
        var videoCount = Directory.Exists(videosDir)
            ? Directory.EnumerateFiles(videosDir, "*.*", SearchOption.TopDirectoryOnly).Count(IsVideoFile)
            : 0;

        var baseDir = (preferProjectMaterials || materialVideoCount > 0) &&
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

        return BuildPublishItemsFromFiles(files, options);
    }

    private static IReadOnlyList<PublishVideoItem> BuildPublishItemsFromFiles(
        IEnumerable<string> paths,
        WeixinVideoPublishOptions options,
        bool preserveOrder = false)
    {
        var candidates = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .Where(path => File.Exists(path) && IsVideoFile(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var files = (preserveOrder
                ? candidates
                : candidates
            .OrderBy(BuildNaturalSortToken, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 0)
        {
            return [];
        }

        var selectedIndexes = ResolveEpisodeIndexes(options, files.Length);
        return selectedIndexes
            .Where(index => index >= 1 && index <= files.Length)
            .Select(index => new PublishVideoItem(index, files[index - 1]))
            .GroupBy(item => item.VideoPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    internal static IReadOnlyList<string> ResolveDirectoryPublishVideoFiles(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            return [];
        }

        return Directory.EnumerateDirectories(projectDir, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(PickLargestDirectoryPublishVideo)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();
    }

    private static string? PickLargestDirectoryPublishVideo(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsVideoFile)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.Length > 0)
            .OrderByDescending(file => file.Length)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".avi", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".flv", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<int> ResolveEpisodeIndexes(WeixinVideoPublishOptions options, int fileCount)
    {
        if (string.Equals(options.EpisodeSelectionMode, "all", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Range(1, fileCount).ToArray();
        }

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
            "custom_files" or "custom" or "files" => PublishVideoSourceModeCustomFiles,
            "downloaded_system_highlight" or "downloaded_system_highlights" or "downloaded_highlight" or "downloaded_highlights" => PublishVideoSourceModeDownloadedSystemHighlight,
            "material_video_download" or "material_download" or "downloaded_material_video" => PublishVideoSourceModeMaterialVideoDownload,
            "directory_publish" or "dir_publish" => PublishVideoSourceModeDirectoryPublish,
            "project_materials" or "project_material" => PublishVideoSourceModeProjectMaterials,
            "source_videos" or "source" => PublishVideoSourceModeSourceVideos,
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

    public static string BuildPublishDescription(
        ProjectInfo projectInfo,
        WeixinVideoPublishOptions options,
        PublishVideoItem? publishItem = null)
    {
        var description = ResolvePerVideoDescription(options, publishItem?.VideoPath);
        if (string.IsNullOrWhiteSpace(description))
        {
            description = !string.IsNullOrWhiteSpace(projectInfo.Tags)
            ? projectInfo.Tags.Trim()
            : (string.IsNullOrWhiteSpace(options.DescriptionTemplate)
                ? "{新剧名}"
                : options.DescriptionTemplate)
                .Replace("{新剧名}", projectInfo.Title, StringComparison.Ordinal)
                .Replace("{原剧名}", projectInfo.OriginalTitle, StringComparison.Ordinal);
        }

        if (options.PrependHashToDescription &&
            !string.IsNullOrWhiteSpace(description) &&
            !description.TrimStart().StartsWith('#'))
        {
            description = "#" + description.TrimStart();
        }

        return description;
    }

    private static string ResolvePerVideoDescription(WeixinVideoPublishOptions options, string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return string.Empty;
        }

        foreach (var key in BuildDescriptionLookupKeys(videoPath))
        {
            if (options.VideoDescriptionMap.TryGetValue(key, out var mapped) &&
                !string.IsNullOrWhiteSpace(mapped))
            {
                return mapped.Trim();
            }
        }

        var sidecar = Path.Combine(
            Path.GetDirectoryName(videoPath) ?? ".",
            Path.GetFileNameWithoutExtension(videoPath) + ".publish.json");
        if (File.Exists(sidecar))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "description", "caption" })
                    {
                        if (document.RootElement.TryGetProperty(key, out var value) &&
                            value.ValueKind == JsonValueKind.String)
                        {
                            var text = value.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        if (string.Equals(NormalizeVideoSourceMode(options.VideoSourceMode), PublishVideoSourceModeDirectoryPublish, StringComparison.Ordinal))
        {
            var directoryDescription = ResolveDirectoryPublishDescription(videoPath);
            if (!string.IsNullOrWhiteSpace(directoryDescription))
            {
                return directoryDescription;
            }
        }

        return string.Empty;
    }

    private static string ResolveDirectoryPublishDescription(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return string.Empty;
        }

        foreach (var fileName in DirectoryPublishDescriptionFileNames)
        {
            var descriptionPath = Path.Combine(directory, fileName);
            if (!File.Exists(descriptionPath))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(descriptionPath).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return NormalizeHashtags(text);
                }
            }
            catch
            {
            }
        }

        return NormalizeHashtags(Path.GetFileName(directory));
    }

    private static string NormalizeHashtags(string text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        value = Regex.Replace(value, @"(?<=[^\s#])#", " #");
        value = Regex.Replace(value, @"[ \t]{2,}", " ");
        return value.Trim();
    }

    private static IEnumerable<string> BuildDescriptionLookupKeys(string videoPath)
    {
        var fileName = Path.GetFileName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        yield return videoPath;
        yield return fileName;
        yield return stem;

        var unprefixed = Regex.Replace(fileName, @"^\d{1,6}[-_ ]+", string.Empty);
        if (!string.Equals(unprefixed, fileName, StringComparison.OrdinalIgnoreCase))
        {
            yield return unprefixed;
            yield return Path.GetFileNameWithoutExtension(unprefixed);
        }
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
