using System.Collections.Concurrent;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed record NewTitleOccupancySnapshot(
    bool Checked,
    IReadOnlyList<string> Titles,
    string SkipReason)
{
    public static NewTitleOccupancySnapshot Skipped(string reason) => new(false, [], reason);

    public static NewTitleOccupancySnapshot Ready(IReadOnlyList<string> titles) => new(true, titles, "");
}

public readonly record struct NewTitleOccupancyDecision(bool Regenerate, string Reason);

/// <summary>新剧名已被占用，重新生成后仍然重复。</summary>
public sealed class NewTitleOccupiedException : InvalidOperationException
{
    public NewTitleOccupiedException(string message) : base(message)
    {
    }
}

/// <summary>
/// 生成新剧名时，只和当前账号的归档、管理系统里已经存在的新剧名做完全相同的比较。
/// 查重失败时不拦截生成。
/// </summary>
public static class NewTitleOccupancyService
{
    public const int MaxRegenerationAttempts = 3;

    private static readonly ConcurrentDictionary<string, string> Reservations = new(StringComparer.OrdinalIgnoreCase);

    public static NewTitleOccupancySnapshot LoadArchiveTitles(
        string? workspaceRoot,
        string? archiveRoot,
        string? currentSourceDir,
        string? currentWorkflowDir)
    {
        try
        {
            var root = (archiveRoot ?? "").Trim();
            if (root.Length == 0)
                return NewTitleOccupancySnapshot.Skipped("未配置归档目录");
            if (!Directory.Exists(root))
                return NewTitleOccupancySnapshot.Skipped("归档目录不存在");

            var workspace = (workspaceRoot ?? "").Trim();
            if (workspace.Length == 0)
                return NewTitleOccupancySnapshot.Skipped("工作目录为空");

            var titles = TikTokArchivedProjectService
                .ListForNewTitleCheck(workspace, root)
                .Where(item => !IsCurrentProject(item, currentSourceDir, currentWorkflowDir))
                .Select(item => (item.NewTitle ?? "").Trim())
                .Where(title => title.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return NewTitleOccupancySnapshot.Ready(titles);
        }
        catch (Exception ex)
        {
            return NewTitleOccupancySnapshot.Skipped(ex.Message);
        }
    }

    public static NewTitleOccupancyDecision Decide(
        string? title,
        NewTitleOccupancySnapshot archive,
        bool managementChecked,
        IEnumerable<string>? managementDuplicates)
    {
        if (archive.Checked && IsExactMatch(title, archive.Titles))
            return new NewTitleOccupancyDecision(true, "归档");
        if (managementChecked && IsExactMatch(title, managementDuplicates))
            return new NewTitleOccupancyDecision(true, "管理系统");
        return new NewTitleOccupancyDecision(false, "");
    }

    public static bool IsExactMatch(string? title, IEnumerable<string>? occupied)
    {
        var key = AiRewriteHistoryService.NormalizeTitleKey(title);
        if (key.Length == 0 || occupied is null)
            return false;

        foreach (var item in occupied)
        {
            if (string.Equals(AiRewriteHistoryService.NormalizeTitleKey(item), key, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static string ReservationAccountKey(TikTokAccountProfile? account)
    {
        var id = (account?.Id ?? "").Trim();
        if (id.Length > 0)
            return "id:" + id;

        var username = FirstNonEmpty(
            account?.TiktokLoginEmail,
            account?.TiktokLastLoginEmail,
            account?.TiktokAccountNickname);
        return username.Length == 0 ? "" : "tt:" + username;
    }

    public static bool TryClaim(string? accountKey, string? title, string? projectDir)
    {
        var key = ReservationKey(accountKey, title);
        var owner = NormalizePath(projectDir);
        if (key.Length == 0 || owner.Length == 0)
            return true;

        if (Reservations.TryGetValue(key, out var existing))
            return string.Equals(existing, owner, StringComparison.OrdinalIgnoreCase);

        return Reservations.TryAdd(key, owner) ||
               (Reservations.TryGetValue(key, out existing) &&
                string.Equals(existing, owner, StringComparison.OrdinalIgnoreCase));
    }

    public static void Release(string? accountKey, string? title, string? projectDir)
    {
        var key = ReservationKey(accountKey, title);
        var owner = NormalizePath(projectDir);
        if (key.Length == 0 || owner.Length == 0)
            return;

        if (Reservations.TryGetValue(key, out var existing) &&
            string.Equals(existing, owner, StringComparison.OrdinalIgnoreCase))
        {
            Reservations.TryRemove(key, out _);
        }
    }

    private static bool IsCurrentProject(
        ArchivedProjectItem item,
        string? currentSourceDir,
        string? currentWorkflowDir)
    {
        return PathsEqual(item.SourceProjectDir, currentSourceDir) ||
               PathsEqual(item.SourceProjectDir, currentWorkflowDir) ||
               PathsEqual(item.WorkflowProjectDir, currentSourceDir) ||
               PathsEqual(item.WorkflowProjectDir, currentWorkflowDir);
    }

    private static string ReservationKey(string? accountKey, string? title)
    {
        var account = (accountKey ?? "").Trim();
        var normalized = AiRewriteHistoryService.NormalizeTitleKey(title);
        if (account.Length == 0 || normalized.Length == 0)
            return "";
        return account + "\n" + normalized;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        var leftKey = NormalizePath(left);
        var rightKey = NormalizePath(right);
        return leftKey.Length > 0 &&
               rightKey.Length > 0 &&
               string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        var text = (path ?? "").Trim();
        if (text.Length == 0)
            return "";
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
        }
        catch
        {
            return text;
        }
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
