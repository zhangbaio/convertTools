using System.Text.Json;
using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>
/// 经 CDP <c>DOM.setFileInputFiles</c> 注入本地路径，避免 ConnectOverCDP 下 Playwright 串流文件触发 50MB 限制。
/// </summary>
internal static class CdpDomFileUpload
{
    private static readonly string[] VideoInputSelectors =
    {
        "input.semi-upload-hidden-input[accept*='mp4']",
        "input.semi-upload-hidden-input[accept*='mov']",
        "input.semi-upload-hidden-input[accept*='video']",
        "input[type=file][accept*='mp4']",
        "input[type=file][accept*='mov']",
        "input[type=file][accept*='video']",
        "input.semi-upload-hidden-input",
        "input.semi-upload-hidden-input-replace",
        "input[type=file]",
    };

    public static async Task<bool> TrySetFilesAsync(IPage page, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return false;
        var normalized = files.Select(Path.GetFullPath).ToArray();

        try
        {
            var client = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
            await client.SendAsync("DOM.enable").ConfigureAwait(false);

            var doc = await client.SendAsync("DOM.getDocument").ConfigureAwait(false);
            var rootNodeId = doc!.Value.GetProperty("root").GetProperty("nodeId").GetInt32();

            foreach (var selector in VideoInputSelectors)
            {
                ct.ThrowIfCancellationRequested();
                var query = await client.SendAsync("DOM.querySelector", new Dictionary<string, object>
                {
                    ["nodeId"] = rootNodeId,
                    ["selector"] = selector,
                }).ConfigureAwait(false);

                if (!query!.Value.TryGetProperty("nodeId", out var nodeIdProp))
                    continue;
                var nodeId = nodeIdProp.GetInt32();
                if (nodeId <= 0) continue;

                try
                {
                    await client.SendAsync("DOM.setFileInputFiles", new Dictionary<string, object>
                    {
                        ["nodeId"] = nodeId,
                        ["files"] = normalized,
                    }).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    // try next selector
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
