using ShortDrama.Infrastructure.AI;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public enum OriginalSynopsisBackfillResult
{
    Added,
    AlreadyPresent,
    SourceMissing,
    InfoFileMissing,
}

public static class ProjectOriginalSynopsisService
{
    internal const string FieldName = "原简介";

    public static OriginalSynopsisBackfillResult Backfill(string workspaceRoot, string projectDir, string? originalTitle)
    {
        var context = ProjectWorkspaceService.LoadContext(projectDir);
        if (!PathsEqual(workspaceRoot, context.WorkspaceRoot))
            throw new InvalidDataException("项目不属于当前工作目录。");

        ProjectWorkspaceService.ValidateContextOwnership(context, repairMovedMetadata: false);
        var infoPath = Path.Combine(context.WorkflowProjectDir, "短剧信息.txt");
        if (!File.Exists(infoPath))
            return OriginalSynopsisBackfillResult.InfoFileMissing;

        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var title = info.GetValueOrDefault("原剧名") ?? originalTitle ?? Path.GetFileName(context.SourceProjectDir);
        if (IsUsable(info.GetValueOrDefault(FieldName), title))
            return OriginalSynopsisBackfillResult.AlreadyPresent;

        var synopsis = Resolve(context, title, info,
            () => AiRewriteHistoryService.LoadForOriginalTitle(title, createIfMissing: false));
        if (synopsis.Length == 0)
            return OriginalSynopsisBackfillResult.SourceMissing;

        return ProjectInfoTextHelper.WriteOriginalSynopsisIfMissing(infoPath, synopsis, title)
            ? OriginalSynopsisBackfillResult.Added
            : OriginalSynopsisBackfillResult.AlreadyPresent;
    }

    internal static string Resolve(
        ProjectWorkspaceContext context,
        string originalTitle,
        IReadOnlyDictionary<string, string> workflowInfo,
        Func<IReadOnlyList<AiRewriteHistoryRecord>>? loadHistory = null)
    {
        var saved = workflowInfo.GetValueOrDefault(FieldName);
        if (IsUsable(saved, originalTitle))
            return ProjectInfoTextHelper.SingleLineSynopsis(saved!);

        var separateSource = !PathsEqual(context.SourceProjectDir, context.WorkflowProjectDir);
        var sourceInfo = separateSource
            ? ProjectInfoTextHelper.ParseInfoFile(Path.Combine(context.SourceProjectDir, "短剧信息.txt"))
            : workflowInfo;
        var sourceTitle = sourceInfo.GetValueOrDefault("原剧名") ?? originalTitle;
        var sourceNewTitle = sourceInfo.GetValueOrDefault("新剧名");
        var sourceInfoIsOriginal = separateSource &&
            (string.IsNullOrWhiteSpace(sourceNewTitle) || SameTitle(sourceNewTitle, sourceTitle));

        var sourceSynopsis = new[]
        {
            sourceInfo.GetValueOrDefault(FieldName),
            ExternalDramaInfoReader.ReadSynopsisFile(Path.Combine(context.SourceProjectDir, "简介.txt")),
            RewriteSourceLanguage.ReadImported(context.SourceProjectDir).Synopsis,
            separateSource ? ExternalDramaInfoReader.ReadSynopsisFile(Path.Combine(context.WorkflowProjectDir, "简介.txt")) : null,
            separateSource ? RewriteSourceLanguage.ReadImported(context.WorkflowProjectDir).Synopsis : null,
            sourceInfoIsOriginal ? sourceInfo.GetValueOrDefault("简介") : null,
            sourceInfoIsOriginal ? sourceInfo.GetValueOrDefault("描述") : null,
            sourceInfoIsOriginal ? sourceInfo.GetValueOrDefault("剧情简介") : null,
        }.FirstOrDefault(text => IsUsable(text, originalTitle));

        if (sourceSynopsis is not null)
            return ProjectInfoTextHelper.SingleLineSynopsis(sourceSynopsis);

        var history = loadHistory?.Invoke() ?? [];
        var original = history.Where(record => IsCurrentProjectHistory(record, context))
            .Where(record => !record.VariantKey.EndsWith("#synopsis=0", StringComparison.OrdinalIgnoreCase) &&
                             !SameTitle(record.OriginalSynopsis, record.NewSynopsis))
            .OrderBy(record => record.CreatedAt, StringComparer.Ordinal)
            .Select(record => record.OriginalSynopsis)
            .FirstOrDefault(text => IsUsable(text, originalTitle));
        return ProjectInfoTextHelper.SingleLineSynopsis(original ?? "");
    }

    internal static bool IsUsable(string? text, string? originalTitle) =>
        !string.IsNullOrWhiteSpace(text) &&
        !text.Contains("待补充", StringComparison.Ordinal) &&
        !text.Contains("暂无简介", StringComparison.Ordinal) &&
        !text.Contains("未填写", StringComparison.Ordinal) &&
        !SameTitle(text, originalTitle);

    private static bool SameTitle(string? left, string? right) =>
        string.Equals(Compact(left), Compact(right), StringComparison.OrdinalIgnoreCase);

    private static string Compact(string? value) =>
        string.Concat((value ?? "").Replace('：', ':').Where(ch => !char.IsWhiteSpace(ch)));

    internal static bool IsCurrentProjectHistory(AiRewriteHistoryRecord record, ProjectWorkspaceContext context) =>
        PathsEqual(record.ProjectDir, context.SourceProjectDir) ||
        PathsEqual(record.ProjectDir, context.WorkflowProjectDir) ||
        PathStartsWith(record.VariantKey, context.SourceProjectDir) ||
        PathStartsWith(record.VariantKey, context.WorkflowProjectDir);

    private static bool PathsEqual(string? left, string? right)
    {
        var leftKey = NormalizePathText(left);
        var rightKey = NormalizePathText(right);
        return leftKey.Length != 0 && rightKey.Length != 0 &&
               string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathStartsWith(string? value, string? path)
    {
        var valueKey = NormalizePathText(value);
        var pathKey = NormalizePathText(path);
        return valueKey.Length != 0 && pathKey.Length != 0 &&
               (string.Equals(valueKey, pathKey, StringComparison.OrdinalIgnoreCase) ||
                valueKey.StartsWith(pathKey + "/", StringComparison.OrdinalIgnoreCase) ||
                valueKey.StartsWith(pathKey + "#", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePathText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        try { text = Path.GetFullPath(text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // Variant keys may append an account suffix. Compare those keys as text.
        }
        return text.Replace('\\', '/').TrimEnd('/');
    }
}
