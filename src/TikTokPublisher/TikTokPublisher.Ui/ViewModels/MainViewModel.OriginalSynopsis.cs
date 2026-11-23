using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public async Task BackfillOriginalSynopsesAsync(IReadOnlyList<QueueProjectRowViewModel> rows)
    {
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            StatusMessage = "请先选择有效的 TikTok 工作目录";
            return;
        }

        var projects = rows.Select(row => row.Item)
            .Where(item => !item.Archived && !string.IsNullOrWhiteSpace(item.ProjectDir))
            .Select(item => (item.ProjectDir, item.OriginalTitle))
            .DistinctBy(item => item.ProjectDir, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (projects.Length == 0)
        {
            StatusMessage = "请先勾选或选中要补齐原简介的项目";
            return;
        }

        // Use the existing workspace reservation so a queue run cannot rewrite the same file concurrently.
        using var reservation = _queueExecution.TryReserveWorkspaceForDeletion(workspace);
        if (reservation is null)
        {
            StatusMessage = "该工作目录正在执行队列或文件操作，请结束后再补齐原简介";
            return;
        }

        StatusMessage = "正在从本地资料补齐原简介…";
        var results = await Task.Run(() => projects.Select(project =>
        {
            try
            {
                var result = ProjectOriginalSynopsisService.Backfill(workspace, project.ProjectDir, project.OriginalTitle);
                return (Result: (OriginalSynopsisBackfillResult?)result, Error: "");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                return (Result: (OriginalSynopsisBackfillResult?)null, Error: $"{project.OriginalTitle}：{ex.Message}");
            }
        }).ToArray());

        foreach (var error in results.Where(row => row.Result is null))
            AppendLog($"补齐原简介失败：{error.Error}");

        var summary = $"原简介已补齐 {results.Count(row => row.Result == OriginalSynopsisBackfillResult.Added)} 项，" +
                      $"已有 {results.Count(row => row.Result == OriginalSynopsisBackfillResult.AlreadyPresent)} 项，" +
                      $"无可靠来源 {results.Count(row => row.Result == OriginalSynopsisBackfillResult.SourceMissing)} 项，" +
                      $"缺少短剧信息 {results.Count(row => row.Result == OriginalSynopsisBackfillResult.InfoFileMissing)} 项，" +
                      $"失败 {results.Count(row => row.Result is null)} 项。";
        if (IsActiveWorkspace(workspace))
            StatusMessage = summary;
        AppendLog(summary);
    }
}
