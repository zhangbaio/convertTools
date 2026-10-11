using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed record DramaDownloadExistenceResult(
    bool ArchiveChecked,
    string ArchiveMessage,
    bool ManagementChecked,
    string ManagementMessage,
    IReadOnlySet<string> ArchiveKeys,
    IReadOnlySet<string> ManagementKeys)
{
    public string LabelFor(string? title)
    {
        var key = AiRewriteHistoryService.NormalizeTitleKey(title);
        if (key.Length == 0)
            return "-";
        var inArchive = ArchiveChecked && ArchiveKeys.Contains(key);
        var inManagement = ManagementChecked && ManagementKeys.Contains(key);
        return Format(ArchiveChecked, inArchive, ManagementChecked, inManagement);
    }

    public static string Format(bool archiveChecked, bool inArchive, bool managementChecked, bool inManagement)
    {
        var parts = new List<string>();
        if (!archiveChecked)
            parts.Add("归档未核对");
        else if (inArchive)
            parts.Add("已归档");

        if (!managementChecked)
            parts.Add("管理系统未核对");
        else if (inManagement)
            parts.Add("管理系统");

        return parts.Count == 0 ? "不存在" : string.Join("、", parts);
    }
}

public static class DramaDownloadExistenceService
{
    public static async Task<DramaDownloadExistenceResult> CheckAsync(
        IReadOnlyList<string> titles,
        TikTokAccountProfile? account,
        CancellationToken cancellationToken)
    {
        var archive = CollectArchive(account);
        var management = await LoadManagementAsync(titles, account, cancellationToken).ConfigureAwait(false);
        return new DramaDownloadExistenceResult(
            archive.Checked,
            archive.Message,
            management.Checked,
            management.Message,
            archive.Keys,
            management.Keys);
    }

    internal static (bool Checked, string Message, HashSet<string> Keys) CollectArchive(TikTokAccountProfile? account)
    {
        if (account is null)
            return (false, "没有选中 TikTok 账号", new HashSet<string>(StringComparer.Ordinal));

        var workspace = account.ResolveWorkspacePath();
        if (string.IsNullOrWhiteSpace(workspace))
            return (false, "当前账号没有上传工作目录", new HashSet<string>(StringComparer.Ordinal));

        string archiveRoot;
        try
        {
            archiveRoot = account.ResolveArchiveRootPath(workspace);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, new HashSet<string>(StringComparer.Ordinal));
        }

        if (string.IsNullOrWhiteSpace(archiveRoot) || !Directory.Exists(archiveRoot))
            return (false, "当前账号没有归档目录", new HashSet<string>(StringComparer.Ordinal));

        try
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in TikTokArchivedProjectService.ListForNewTitleCheck(workspace, archiveRoot))
            {
                AddKey(keys, item.OriginalTitle);
                AddKey(keys, Path.GetFileName(item.ArchiveProjectDir));
            }

            return (true, "", keys);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, new HashSet<string>(StringComparer.Ordinal));
        }
    }

    private static async Task<(bool Checked, string Message, HashSet<string> Keys)> LoadManagementAsync(
        IReadOnlyList<string> titles,
        TikTokAccountProfile? account,
        CancellationToken cancellationToken)
    {
        var username = FirstNonEmpty(
            account?.TiktokLoginEmail,
            account?.TiktokLastLoginEmail,
            account?.TiktokAccountNickname);
        if (string.IsNullOrWhiteSpace(username))
            return (false, "当前账号没有 TikTok 用户名", new HashSet<string>(StringComparer.Ordinal));

        var check = await TikTokManagementUploadRecordSyncService.CheckDuplicateOriginalNamesAsync(
            titles,
            "tiktok_username",
            account,
            cancellationToken,
            normalizeTitles: true).ConfigureAwait(false);
        if (!check.Ok)
            return (false, check.Message, new HashSet<string>(StringComparer.Ordinal));

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in check.Duplicates)
            AddKey(keys, name);
        return (true, "", keys);
    }

    private static void AddKey(ISet<string> keys, string? title)
    {
        var key = AiRewriteHistoryService.NormalizeTitleKey(title);
        if (key.Length > 0)
            keys.Add(key);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (text.Length > 0)
                return text;
        }

        return "";
    }
}
