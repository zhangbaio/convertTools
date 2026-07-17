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
        var normalized = files.Select(Path.GetFullPath).Where(File.Exists).ToArray();
        if (normalized.Length == 0) return false;

        try
        {
            var client = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
            await client.SendAsync("DOM.enable").ConfigureAwait(false);

            var doc = await client.SendAsync("DOM.getDocument").ConfigureAwait(false);
            var rootNodeId = doc!.Value.GetProperty("root").GetProperty("nodeId").GetInt32();

            var candidates = new List<(int NodeId, int Score)>();
            foreach (var selector in VideoInputSelectors)
            {
                ct.ThrowIfCancellationRequested();
                var nodes = await client.SendAsync("DOM.querySelectorAll", new Dictionary<string, object>
                {
                    ["nodeId"] = rootNodeId,
                    ["selector"] = selector,
                }).ConfigureAwait(false);

                if (!nodes!.Value.TryGetProperty("nodeIds", out var nodeIdsProp) ||
                    nodeIdsProp.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;

                foreach (var nodeIdElement in nodeIdsProp.EnumerateArray())
                {
                    var nodeId = nodeIdElement.GetInt32();
                    if (nodeId <= 0) continue;
                    candidates.Add((nodeId, ScoreVideoInputSelector(selector)));
                }
            }

            foreach (var (nodeId, _) in candidates.OrderByDescending(item => item.Score))
            {
                ct.ThrowIfCancellationRequested();
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
                    // try next candidate
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static int ScoreVideoInputSelector(string selector)
    {
        var normalized = selector.ToLowerInvariant();
        var score = 0;
        if (normalized.Contains("mp4") || normalized.Contains("mov") || normalized.Contains("video"))
            score += 5;
        if (normalized.Contains("semi-upload-hidden-input") && !normalized.Contains("replace"))
            score += 3;
        if (normalized.Contains("replace"))
            score -= 1;
        if (normalized.Contains("type=file"))
            score += 1;
        return score;
    }
}
