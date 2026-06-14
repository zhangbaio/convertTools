using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Workflow;

public sealed class ProjectScanner : IProjectScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".mkv",
        ".avi",
        ".flv",
        ".wmv",
        ".webm"
    };

    private const int DefaultStepCount = 8;

    private readonly IProjectInfoParser _projectInfoParser;

    public ProjectScanner(IProjectInfoParser projectInfoParser)
    {
        _projectInfoParser = projectInfoParser;
    }

    public async Task<ProjectScanResult> ScanAsync(
        string rootDir,
        string? backupRootDir,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDir))
        {
            throw new DirectoryNotFoundException($"根目录不存在: {rootDir}");
        }

        var resolvedBackupRoot = ResolveBackupRoot(rootDir, backupRootDir);
        var workflowRoot = Path.Combine(rootDir, "workflow");
        var sourceDirs = Directory.EnumerateDirectories(rootDir, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !IsReservedRootChild(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workflowCandidates = Directory.Exists(workflowRoot)
            ? Directory.EnumerateDirectories(workflowRoot, "*", SearchOption.TopDirectoryOnly).ToList()
            : [];

        var projects = new List<ScannedProject>();

        foreach (var sourceDir in sourceDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceName = Path.GetFileName(sourceDir);
            var sourceMetadata = ProjectAutomationMetadata.Resolve(sourceDir);
            var backupDir = resolvedBackupRoot is not null
                ? Path.Combine(resolvedBackupRoot, sourceName)
                : null;

            var projectInfo = await TryParseProjectInfoAsync(sourceDir, cancellationToken)
                ?? await TryParseProjectInfoAsync(backupDir, cancellationToken);

            var displayName = sourceMetadata.DisplayName
                ?? sourceMetadata.Title
                ?? projectInfo?.Title
                ?? sourceName;
            var workflowDir = await ResolveWorkflowDirectoryAsync(
                rootDir,
                sourceDir,
                sourceName,
                displayName,
                sourceMetadata,
                workflowCandidates,
                cancellationToken);
            var workflowInfo = await TryParseProjectInfoAsync(workflowDir, cancellationToken);
            if (workflowInfo is not null)
            {
                projectInfo = workflowInfo;
                displayName = workflowInfo.Title;
            }

            var state = ResolveState(sourceDir, workflowDir, backupDir);
            var videoCount = CountVideoFiles(sourceDir);
            var createdAt = sourceMetadata.CreatedAt ?? TryGetDirectoryCreatedAt(sourceDir);

            projects.Add(new ScannedProject(
                ProjectKey: sourceName,
                SourceName: sourceName,
                DisplayName: displayName,
                SourceProjectDir: sourceDir,
                WorkflowProjectDir: workflowDir,
                BackupProjectDir: backupDir is not null && Directory.Exists(backupDir) ? backupDir : null,
                CreatedAt: createdAt,
                Status: state.Status,
                VideoCount: videoCount,
                CompletedSteps: state.CompletedSteps,
                TotalSteps: state.TotalSteps,
                ResumeFrom: state.ResumeFrom,
                FailedStep: state.FailedStep,
                HasFailure: state.HasFailure));
        }

        return new ProjectScanResult(
            RootDir: rootDir,
            BackupRootDir: resolvedBackupRoot,
            TotalProjects: projects.Count,
            PendingProjects: projects.Count(project => !string.Equals(project.Status, "已完成", StringComparison.Ordinal)),
            Projects: projects);
    }

    private static bool IsReservedRootChild(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(".", StringComparison.Ordinal) ||
            string.Equals(name, "workflow", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "archive", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "config", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveBackupRoot(string rootDir, string? backupRootDir)
    {
        if (!string.IsNullOrWhiteSpace(backupRootDir))
        {
            return Directory.Exists(backupRootDir) ? backupRootDir : null;
        }

        var parent = Directory.GetParent(rootDir);
        if (parent is null)
        {
            return null;
        }

        var siblingBackup = Path.Combine(parent.FullName, "backup");
        return Directory.Exists(siblingBackup) ? siblingBackup : null;
    }

    private static DateTimeOffset? TryGetDirectoryCreatedAt(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            return Directory.GetCreationTimeUtc(path) switch
            {
                var utc when utc == DateTime.MinValue => null,
                var utc => new DateTimeOffset(utc, TimeSpan.Zero)
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<ProjectInfo?> TryParseProjectInfoAsync(string? projectDir, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            return null;
        }

        try
        {
            return await _projectInfoParser.ParseAsync(projectDir, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> FindWorkflowDirectoryAsync(
        string sourceName,
        string displayName,
        IReadOnlyList<string> workflowCandidates,
        CancellationToken cancellationToken)
    {
        string NormalizeName(string value) => value.TrimStart('_');

        var direct = workflowCandidates.FirstOrDefault(path =>
            string.Equals(NormalizeName(Path.GetFileName(path)), sourceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeName(Path.GetFileName(path)), displayName, StringComparison.OrdinalIgnoreCase));

        if (direct is not null)
        {
            return direct;
        }

        foreach (var candidate in workflowCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = await _projectInfoParser.ParseAsync(candidate, cancellationToken);
                if (string.Equals(info.OriginalTitle, sourceName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.Title, sourceName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.Title, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid or incomplete workflow folders during scan.
            }
        }

        return null;
    }

    private async Task<string?> ResolveWorkflowDirectoryAsync(
        string rootDir,
        string sourceDir,
        string sourceName,
        string displayName,
        ProjectAutomationMetadata sourceMetadata,
        IReadOnlyList<string> workflowCandidates,
        CancellationToken cancellationToken)
    {
        var workflowRoot = Path.Combine(rootDir, "workflow");
        var metadataPath = ResolveWorkflowPathFromMetadata(sourceMetadata, workflowRoot);
        if (!string.IsNullOrWhiteSpace(metadataPath) && Directory.Exists(metadataPath))
        {
            return metadataPath;
        }

        var statePath = Path.Combine(sourceDir, "shortdrama-state.json");
        if (File.Exists(statePath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(statePath));
                var root = document.RootElement;
                var workflowProjectDir = GetString(root, "workflowProjectDir") ?? GetString(root, "workflow_project_dir");
                if (!string.IsNullOrWhiteSpace(workflowProjectDir) && Directory.Exists(workflowProjectDir))
                {
                    return workflowProjectDir;
                }
            }
            catch
            {
                // Ignore malformed state files and fall back to legacy inference.
            }
        }

        return await FindWorkflowDirectoryAsync(sourceName, displayName, workflowCandidates, cancellationToken);
    }

    private static string? ResolveWorkflowPathFromMetadata(ProjectAutomationMetadata metadata, string workflowRoot)
    {
        if (!string.IsNullOrWhiteSpace(metadata.WorkflowProjectDir))
        {
            return metadata.WorkflowProjectDir;
        }

        if (!string.IsNullOrWhiteSpace(metadata.WorkflowDirName))
        {
            return Path.Combine(workflowRoot, metadata.WorkflowDirName);
        }

        return null;
    }

    private static int CountVideoFiles(string projectDir)
    {
        return Directory.EnumerateFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly)
            .Count(path => VideoExtensions.Contains(Path.GetExtension(path)));
    }

    private static ResolvedState ResolveState(string sourceDir, string? workflowDir, string? backupDir)
    {
        var workflowState = ReadState(workflowDir);
        var backupState = ReadState(backupDir);
        var sourceState = ReadState(sourceDir);

        if (workflowDir is not null)
        {
            var workflowName = Path.GetFileName(workflowDir);
            var outputsCompleted = IsCompletedByOutputs(workflowDir);
            if (!workflowName.StartsWith('_') && IsCompletedByOutputs(workflowDir))
            {
                return new ResolvedState("已完成", DefaultStepCount, DefaultStepCount, "workflow", null, false);
            }

            if (!workflowName.StartsWith('_') && workflowState.AllCompleted && outputsCompleted)
            {
                return new ResolvedState("已完成", workflowState.CompletedSteps, workflowState.TotalSteps, "workflow", null, false);
            }

            if (!workflowName.StartsWith('_') && workflowState.AllCompleted && !outputsCompleted)
            {
                return new ResolvedState("可恢复", workflowState.CompletedSteps, workflowState.TotalSteps, "workflow", null, false);
            }

            if (workflowState.HasFailure)
            {
                return new ResolvedState("失败可重试", workflowState.CompletedSteps, workflowState.TotalSteps, "workflow", workflowState.FailedStep, true);
            }

            if (workflowState.WasCancelled)
            {
                return new ResolvedState("可恢复", workflowState.CompletedSteps, workflowState.TotalSteps, "workflow", workflowState.FailedStep, false);
            }

            if (workflowName.StartsWith('_') || workflowState.CompletedSteps > 0)
            {
                return new ResolvedState("处理中", workflowState.CompletedSteps, workflowState.TotalSteps, "workflow", null, false);
            }
        }

        if (backupState.CompletedSteps > 0 || backupState.HasFailure)
        {
            return new ResolvedState(
                backupState.HasFailure ? "失败可重试" : "可恢复",
                backupState.CompletedSteps,
                backupState.TotalSteps,
                "backup",
                backupState.FailedStep,
                backupState.HasFailure);
        }

        if (backupState.WasCancelled)
        {
            return new ResolvedState(
                "可恢复",
                backupState.CompletedSteps,
                backupState.TotalSteps,
                "backup",
                backupState.FailedStep,
                false);
        }

        if (sourceState.CompletedSteps > 0 || sourceState.HasFailure)
        {
            return new ResolvedState(
                sourceState.HasFailure ? "失败可重试" : "可恢复",
                sourceState.CompletedSteps,
                sourceState.TotalSteps,
                "source",
                sourceState.FailedStep,
                sourceState.HasFailure);
        }

        if (sourceState.WasCancelled)
        {
            return new ResolvedState(
                "可恢复",
                sourceState.CompletedSteps,
                sourceState.TotalSteps,
                "source",
                sourceState.FailedStep,
                false);
        }

        return new ResolvedState("未开始", 0, DefaultStepCount, null, null, false);
    }

    private static bool IsCompletedByOutputs(string workflowDir)
    {
        if (!Directory.Exists(workflowDir))
        {
            return false;
        }

        var infoExists = File.Exists(Path.Combine(workflowDir, "短剧信息.txt"));
        var posterExists = File.Exists(Path.Combine(workflowDir, "海报图片.jpg"));
        var costExists = File.Exists(Path.Combine(workflowDir, "成本报表.png"));
        var projectImages = Directory.EnumerateFiles(workflowDir, "工程图_*.png", SearchOption.TopDirectoryOnly).Any();
        var renamedVideos = Directory.Exists(Path.Combine(workflowDir, "videos")) &&
            Directory.EnumerateFiles(Path.Combine(workflowDir, "videos"), "*.*", SearchOption.TopDirectoryOnly)
                .Any(path => VideoExtensions.Contains(Path.GetExtension(path)) &&
                             Path.GetFileNameWithoutExtension(path).Contains("-第", StringComparison.Ordinal));

        return infoExists && posterExists && costExists && projectImages && renamedVideos;
    }

    private static StateSummary ReadState(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            return StateSummary.Empty;
        }

        var statePath = ResolveStatePath(projectDir);
        if (!File.Exists(statePath))
        {
            return StateSummary.Empty;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(statePath));
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return ParseStepArray(root);
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("steps", out var steps) &&
            steps.ValueKind == JsonValueKind.Array)
        {
            return ParseStepArray(steps);
        }

        return StateSummary.Empty;
    }

    private static string ResolveStatePath(string projectDir)
    {
        var sourceStatePath = Path.Combine(projectDir, "shortdrama-state.json");
        if (File.Exists(sourceStatePath))
        {
            return sourceStatePath;
        }

        return Path.Combine(projectDir, "states.json");
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static StateSummary ParseStepArray(JsonElement steps)
    {
        var completed = 0;
        var total = 0;
        var failedStep = default(string);
        var wasCancelled = false;

        foreach (var step in steps.EnumerateArray())
        {
            total++;

            var isCompleted = step.TryGetProperty("isCompleted", out var completedElement) &&
                completedElement.ValueKind == JsonValueKind.True;

            if (isCompleted)
            {
                completed++;
            }

            if (failedStep is null &&
                step.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errorElement.GetString()))
            {
                failedStep = step.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                var error = errorElement.GetString()!;
                wasCancelled = error.Contains("已停止", StringComparison.Ordinal) ||
                               error.Contains("可继续运行", StringComparison.Ordinal) ||
                               error.Contains("未下载完成", StringComparison.Ordinal);
            }
        }

        total = Math.Max(total, DefaultStepCount);
        return new StateSummary(
            CompletedSteps: completed,
            TotalSteps: total,
            FailedStep: failedStep,
            WasCancelled: wasCancelled);
    }

    private sealed record ResolvedState(
        string Status,
        int CompletedSteps,
        int TotalSteps,
        string? ResumeFrom,
        string? FailedStep,
        bool HasFailure);

    private sealed record StateSummary(
        int CompletedSteps,
        int TotalSteps,
        string? FailedStep,
        bool WasCancelled)
    {
        public static StateSummary Empty { get; } = new(0, DefaultStepCount, null, false);

        public bool HasFailure => !WasCancelled && !string.IsNullOrWhiteSpace(FailedStep);
        public bool AllCompleted => CompletedSteps >= TotalSteps && !HasFailure;
    }
}
