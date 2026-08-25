using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>
/// 经 CDP <c>DOM.setFileInputFiles</c> 注入本地路径，避免 ConnectOverCDP 下 Playwright 串流文件触发 50MB 限制。
/// </summary>
internal static class CdpDomFileUpload
{
    private const string TargetAttribute = "data-yunfan-video-upload-target";

    public static async Task<bool> TrySetFilesAsync(
        IPage page,
        ILocator input,
        IReadOnlyList<string> files,
        CancellationToken ct = default)
    {
        if (files.Count == 0) return false;
        var normalized = files.Select(Path.GetFullPath).Where(File.Exists).ToArray();
        if (normalized.Length != files.Count) return false;

        var marker = Guid.NewGuid().ToString("N");
        ICDPSession? client = null;
        try
        {
            await input.EvaluateAsync<string>(
                "(element, value) => { element.setAttribute('data-yunfan-video-upload-target', value); return value; }",
                marker).ConfigureAwait(false);

            client = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
            await client.SendAsync("DOM.enable").ConfigureAwait(false);

            var doc = await client.SendAsync("DOM.getDocument").ConfigureAwait(false);
            var rootNodeId = doc!.Value.GetProperty("root").GetProperty("nodeId").GetInt32();
            var node = await client.SendAsync("DOM.querySelector", new Dictionary<string, object>
            {
                ["nodeId"] = rootNodeId,
                ["selector"] = $"input[{TargetAttribute}='{marker}']",
            }).ConfigureAwait(false);
            var nodeId = node!.Value.GetProperty("nodeId").GetInt32();
            if (nodeId <= 0) return false;

            ct.ThrowIfCancellationRequested();
            await client.SendAsync("DOM.setFileInputFiles", new Dictionary<string, object>
            {
                ["nodeId"] = nodeId,
                ["files"] = normalized,
            }).ConfigureAwait(false);

            var selectedCount = await input.EvaluateAsync<int>(
                "element => element.files ? element.files.length : 0").ConfigureAwait(false);
            return selectedCount == normalized.Length;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (client is not null)
            {
                try { await client.DetachAsync().ConfigureAwait(false); }
                catch { /* page may already be closing */ }
            }
            try
            {
                await input.EvaluateAsync(
                    "element => element.removeAttribute('data-yunfan-video-upload-target')")
                    .ConfigureAwait(false);
            }
            catch { /* input may have been replaced after the change event */ }
        }
    }
}
