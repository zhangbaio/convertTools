using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services;

public sealed record TikTokUploadFailureSnapshot(
    string DirectoryPath,
    string? ScreenshotPath,
    string? BodyTextPath,
    string? HtmlPath,
    string? MetadataPath);

public static class TikTokUploadFailureSnapshotService
{
    private const int TextReadTimeoutMs = 5000;
    private const int ScreenshotTimeoutMs = 15000;

    public static async Task<TikTokUploadFailureSnapshot?> CaptureAsync(
        IPage? page,
        string? workflowProjectDir,
        string failure,
        string? title,
        string? accountName,
        Action<string>? log)
    {
        if (page is null || string.IsNullOrWhiteSpace(workflowProjectDir))
            return null;

        try
        {
            var root = Path.GetFullPath(workflowProjectDir);
            var snapshotDir = Path.Combine(
                root,
                "upload-failure-snapshots",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(snapshotDir);

            var errors = new List<string>();
            var url = SafeRead(() => page.Url, "");
            var pageTitle = await TryReadAsync(
                    () => page.TitleAsync(),
                    "",
                    errors,
                    "read title")
                .ConfigureAwait(false);

            var bodyText = await TryReadAsync(
                    () => page.Locator("body").InnerTextAsync(new() { Timeout = TextReadTimeoutMs }),
                    "",
                    errors,
                    "read body text")
                .ConfigureAwait(false);
            var diagnostics = await TryReadAsync(
                    () => ReadDiagnosticsAsync(page),
                    "",
                    errors,
                    "read diagnostics")
                .ConfigureAwait(false);
            var html = await TryReadAsync(
                    () => page.ContentAsync(),
                    "",
                    errors,
                    "read html")
                .ConfigureAwait(false);

            var bodyPath = Path.Combine(snapshotDir, "body.txt");
            await File.WriteAllTextAsync(bodyPath, bodyText ?? "", Encoding.UTF8).ConfigureAwait(false);

            var diagnosticsPath = Path.Combine(snapshotDir, "diagnostics.txt");
            await File.WriteAllTextAsync(diagnosticsPath, diagnostics ?? "", Encoding.UTF8).ConfigureAwait(false);

            var htmlPath = Path.Combine(snapshotDir, "page.html");
            await File.WriteAllTextAsync(htmlPath, html ?? "", Encoding.UTF8).ConfigureAwait(false);

            string? screenshotPath = Path.Combine(snapshotDir, "screenshot.png");
            try
            {
                await page.ScreenshotAsync(new()
                {
                    Path = screenshotPath,
                    FullPage = true,
                    Timeout = ScreenshotTimeoutMs,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"screenshot: {ex.GetType().Name}: {ex.Message}");
                screenshotPath = null;
            }

            var metadataPath = Path.Combine(snapshotDir, "metadata.json");
            var metadata = new Dictionary<string, object?>
            {
                ["captured_at"] = DateTimeOffset.Now.ToString("o"),
                ["workflow_project_dir"] = root,
                ["title"] = title ?? "",
                ["account"] = accountName ?? "",
                ["url"] = url,
                ["page_title"] = pageTitle ?? "",
                ["failure"] = failure ?? "",
                ["screenshot"] = screenshotPath is null ? "" : Path.GetFileName(screenshotPath),
                ["body_text"] = Path.GetFileName(bodyPath),
                ["diagnostics"] = Path.GetFileName(diagnosticsPath),
                ["html"] = Path.GetFileName(htmlPath),
                ["capture_errors"] = errors,
            };
            await File.WriteAllTextAsync(
                    metadataPath,
                    JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8)
                .ConfigureAwait(false);

            if (errors.Count > 0)
            {
                await File.WriteAllLinesAsync(
                        Path.Combine(snapshotDir, "capture-errors.txt"),
                        errors,
                        Encoding.UTF8)
                    .ConfigureAwait(false);
            }

            log?.Invoke($"Saved TikTok upload failure page snapshot: {snapshotDir}");
            return new TikTokUploadFailureSnapshot(
                snapshotDir,
                screenshotPath,
                bodyPath,
                htmlPath,
                metadataPath);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to save TikTok upload failure page snapshot: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<T> TryReadAsync<T>(
        Func<Task<T>> read,
        T fallback,
        List<string> errors,
        string label)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
            return fallback;
        }
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static async Task<string> ReadDiagnosticsAsync(IPage page)
    {
        return await page.EvaluateAsync<string>(
            """
            () => {
              const selectors = [
                '[role="alert"]',
                '[role="dialog"]',
                '.semi-modal',
                '.semi-toast',
                '.semi-notification',
                '.semi-upload',
                '.semi-upload-file-list',
                '.semi-table',
                '.semi-table-body',
                '.semi-form',
                '.semi-spin',
                '.semi-banner'
              ];
              const seen = new Set();
              const parts = [];
              for (const selector of selectors) {
                for (const el of document.querySelectorAll(selector)) {
                  const text = (el.innerText || el.textContent || '').trim();
                  if (!text || seen.has(selector + '\n' + text)) continue;
                  seen.add(selector + '\n' + text);
                  parts.push(`### ${selector}\n${text}`);
                }
              }
              return parts.join('\n\n');
            }
            """).ConfigureAwait(false);
    }
}
