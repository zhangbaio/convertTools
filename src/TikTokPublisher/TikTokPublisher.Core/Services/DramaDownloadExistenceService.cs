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
    public bool ArchiveSkipped { get; init; }
    internal bool ArchiveIncomplete => !ArchiveChecked && !ArchiveSkipped;

    public string LabelFor(string? title)
    {
        var key = AiRewriteHistoryService.NormalizeTitleKey(title);
        if (key.Length == 0)
            return "-";
        var inArchive = ArchiveChecked && ArchiveKeys.Contains(key);
        var inManagement = ManagementChecked && ManagementKeys.Contains(key);
        return Format(!ArchiveIncomplete, inArchive, ManagementChecked, inManagement);
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

    public static DramaExistenceTone ToneFor(string? text)
    {
        if (text?.StartsWith(DramaDownloadAllAccountsExistenceResult.PresentPrefix, StringComparison.Ordinal) == true)
            return DramaExistenceTone.Present;
        var parts = (text ?? "").Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => part is "已归档" or "管理系统"))
            return DramaExistenceTone.Present;
        if (parts.Length == 1 && parts[0] == "不存在")
            return DramaExistenceTone.Absent;
        return DramaExistenceTone.Unknown;
    }
}

public sealed record DramaDownloadAccountExistence(string AccountName, DramaDownloadExistenceResult Result);

public sealed record DramaDownloadAllAccountsExistenceResult(
    IReadOnlyList<DramaDownloadAccountExistence> Accounts,
    IReadOnlySet<string> UnassignedArchiveKeys)
{
    internal const string PresentPrefix = "已存在 · ";

    public string LabelFor(string? title)
    {
        var key = AiRewriteHistoryService.NormalizeTitleKey(title);
        if (key.Length == 0)
            return "-";
        if (Accounts.Count == 0)
            return "未配置 TikTok 账号";

        var hits = new List<string>();
        foreach (var (accountName, result) in Accounts)
        {
            var inArchive = result.ArchiveChecked && result.ArchiveKeys.Contains(key);
            var inManagement = result.ManagementChecked && result.ManagementKeys.Contains(key);
            if (inArchive || inManagement)
            {
                var sources = DramaDownloadExistenceResult.Format(true, inArchive, true, inManagement);
                hits.Add($"{accountName}（{sources}）");
            }
        }
        if (UnassignedArchiveKeys.Contains(key))
            hits.Add("归属未标记的归档");

        var incomplete = Accounts.Any(account => account.Result.ArchiveIncomplete || !account.Result.ManagementChecked);
        if (hits.Count == 0)
            return incomplete ? "未核对完整" : "不存在";
        return PresentPrefix + string.Join("、", hits) + (incomplete ? " · 部分来源未核对" : "");
    }
}

public enum DramaExistenceTone
{
    Unknown,
    Absent,
    Present,
}

public static class DramaDownloadExistenceService
{
    public static Task<DramaDownloadAllAccountsExistenceResult> CheckAllAsync(
        IReadOnlyList<string> titles, IReadOnlyList<TikTokAccountProfile> accounts, CancellationToken cancellationToken) =>
        CheckAllAsync(titles, accounts, LoadManagementAsync, cancellationToken);

    internal static async Task<DramaDownloadAllAccountsExistenceResult> CheckAllAsync(
        IReadOnlyList<string> titles,
        IReadOnlyList<TikTokAccountProfile> accounts,
        Func<IReadOnlyList<string>, TikTokAccountProfile?, CancellationToken,
            Task<(bool Checked, string Message, HashSet<string> Keys)>> loadManagement,
        CancellationToken cancellationToken)
    {
        var locations = await Task.Run(() => accounts
            .DistinctBy(account => FirstNonEmpty(account.Id, account.ResolveTikTokAccountName(), account.DisplayName),
                StringComparer.OrdinalIgnoreCase)
            .Select(account =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workspace = account.ResolveWorkspacePath();
                return (Account: account, Workspace: workspace,
                    Root: Path.TrimEndingDirectorySeparator(account.ResolveArchiveRootPath(workspace)));
            }).ToArray(), cancellationToken).ConfigureAwait(false);
        var sharedRoots = FindDuplicates(locations.Select(location => location.Root));
        var duplicateNames = FindDuplicates(locations.Select(location => location.Account.DisplayName));
        var results = new DramaDownloadAccountExistence[locations.Length];
        var unassigned = new HashSet<string>[locations.Length];

        await Parallel.ForEachAsync(Enumerable.Range(0, locations.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = 3,
            CancellationToken = cancellationToken,
        }, async (index, ct) =>
        {
            var (account, workspace, root) = locations[index];
            var archive = ReadArchive(workspace, root);
            var archiveKeys = new HashSet<string>(StringComparer.Ordinal);
            var unassignedKeys = new HashSet<string>(StringComparer.Ordinal);
            var shared = sharedRoots.Contains(root);
            foreach (var item in archive.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(item.ArchiveSource) &&
                    !string.Equals(item.ArchiveSource, "tiktok", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (shared && string.IsNullOrWhiteSpace(item.AccountProfileId) &&
                    (string.IsNullOrWhiteSpace(item.AccountProfileName) || duplicateNames.Contains(item.AccountProfileName)))
                {
                    AddArchiveKeys(unassignedKeys, item);
                }
                else if (TikTokArchivedProjectService.MatchesAccount(item, account.Id, account.DisplayName, allowUnassigned: !shared))
                {
                    AddArchiveKeys(archiveKeys, item);
                }
            }

            (bool Checked, string Message, HashSet<string> Keys) management;
            try
            {
                management = await loadManagement(titles, account, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                management = (false, ex is OperationCanceledException ? "连接管理系统超时" : ex.Message, []);
            }
            var name = duplicateNames.Contains(account.DisplayName) && !string.IsNullOrWhiteSpace(account.Id)
                ? $"{account.DisplayName} [{account.Id}]" : account.DisplayName;
            results[index] = new(name, new DramaDownloadExistenceResult(
                archive.Checked, archive.Message, management.Checked, management.Message, archiveKeys, management.Keys)
            {
                ArchiveSkipped = archive.Skipped,
            });
            unassigned[index] = unassignedKeys;
        }).ConfigureAwait(false);

        return new(results, unassigned.SelectMany(keys => keys).ToHashSet(StringComparer.Ordinal));
    }

    private static HashSet<string> FindDuplicates(IEnumerable<string> values) =>
        values.Where(value => value.Length != 0)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            management.Keys)
        {
            ArchiveSkipped = archive.Skipped,
        };
    }

    internal static (bool Checked, bool Skipped, string Message, HashSet<string> Keys) CollectArchive(TikTokAccountProfile? account)
    {
        if (account is null)
            return (false, false, "没有选中 TikTok 账号", new HashSet<string>(StringComparer.Ordinal));

        var workspace = account.ResolveWorkspacePath();
        var archive = ReadArchive(workspace, account.ResolveArchiveRootPath(workspace));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in archive.Items)
            AddArchiveKeys(keys, item);
        return (archive.Checked, archive.Skipped, archive.Message, keys);
    }

    private static (bool Checked, bool Skipped, string Message, IReadOnlyList<ArchivedProjectItem> Items) ReadArchive(
        string workspace, string archiveRoot)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot))
            return (false, true, "", []);

        try
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(archiveRoot);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
            {
                return (false, true, "", []);
            }

            if ((attributes & FileAttributes.Directory) == 0)
                return (false, true, "", []);
            if (string.IsNullOrWhiteSpace(workspace))
                return (false, false, "当前账号没有上传工作目录", []);

            return (true, false, "", TikTokArchivedProjectService.ListForNewTitleCheck(workspace, archiveRoot));
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message, []);
        }
    }

    private static void AddArchiveKeys(ISet<string> keys, ArchivedProjectItem item)
    {
        AddKey(keys, item.OriginalTitle);
        AddKey(keys, Path.GetFileName(item.ArchiveProjectDir));
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
