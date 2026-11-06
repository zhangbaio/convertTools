using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record XingeRemoteAccountSeed(
    string Id,
    string Name,
    string AuthPath,
    bool IsCurrent,
    bool RemoteDispatchEnabled,
    string UploadProfilePath,
    string LastWorkspace,
    string ArchiveRootDir)
{
    public string ResolveWorkspacePath()
    {
        foreach (var candidate in new[] { UploadProfilePath, LastWorkspace })
        {
            var path = (candidate ?? "").Trim();
            if (path.Length == 0)
                continue;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                if (Directory.Exists(full))
                    return full;
            }
            catch
            {
                // 非法路径留给下一个候选。
            }
        }

        return "";
    }

    public string ResolveArchiveRoot(string workspace)
    {
        var configured = (ArchiveRootDir ?? "").Trim();
        if (configured.Length > 0)
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            }
            catch
            {
                return "";
            }
        }

        if (string.IsNullOrWhiteSpace(workspace))
            return "";

        try
        {
            return Path.Combine(Path.GetFullPath(workspace), "archive");
        }
        catch
        {
            return "";
        }
    }
}

public static class XingeUploadedTodayScan
{
    public static Dictionary<string, IReadOnlyList<TikTokUploadedDrama>?> Collect(
        IReadOnlyList<XingeRemoteAccountSeed> accounts,
        bool includeExecutionHistory = true)
    {
        var result = new Dictionary<string, IReadOnlyList<TikTokUploadedDrama>?>(StringComparer.OrdinalIgnoreCase);
        var stamps = includeExecutionHistory
            ? TikTokExecutionHistoryService.ReadUploadCompletionStamps()
            : Array.Empty<UploadCompletionStamp>();
        var workspaces = new Dictionary<string, List<XingeRemoteAccountSeed>>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts)
        {
            string workspace;
            try
            {
                workspace = account.ResolveWorkspacePath();
            }
            catch
            {
                result[account.Id] = null;
                continue;
            }

            if (string.IsNullOrWhiteSpace(workspace))
            {
                result[account.Id] = [];
                continue;
            }

            if (!workspaces.TryGetValue(workspace, out var owners))
            {
                owners = [];
                workspaces[workspace] = owners;
            }

            owners.Add(account);
        }

        foreach (var (workspace, owners) in workspaces)
        {
            try
            {
                var items = WorkspaceQueueService.LoadCompletedUploadItems(workspace);
                var archiveRoot = owners[0].ResolveArchiveRoot(workspace);
                var found = new Dictionary<string, IReadOnlyList<TikTokUploadedDrama>>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in TikTokTodayUploadCountService.ListRecentUploads(items, workspace, archiveRoot, stamps)
                             .GroupBy(item => item.AccountProfileId ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(group.Key))
                        continue;
                    found[group.Key] = group.ToArray();
                }

                foreach (var owner in owners)
                    result[owner.Id] = found.TryGetValue(owner.Id, out var uploads) ? uploads : [];
            }
            catch
            {
                foreach (var owner in owners)
                    result[owner.Id] = null;
            }
        }

        return result;
    }
}
