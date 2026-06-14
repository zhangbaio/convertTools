using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Config;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Workflow;

public sealed class WorkService : IWorkService
{
    private const decimal CostPerMinuteYuan = 1500m;
    private const int MaxProofMaterialImageCount = 4;
    private static readonly string[] DefaultStepTypes =
    [
        "download",
        "transcode",
        "rewrite",
        "poster-rename",
        "project-image",
        "cost-report",
        "batch-file-rename",
        "weixin-upload"
    ];

    private static readonly string[] ProjectMaterialStepTypes =
    [
        "download",
        "transcode",
        "rewrite",
        "poster-rename",
        "project-image",
        "cost-report",
        "batch-file-rename",
        "material-convert"
    ];
    private static readonly string[] WeixinConfigFileNames =
    [
        "weixin-channel-autogen.json",
        "weixin-channel-submit.json",
        "weixin-channel-config.json"
    ];

    private readonly IProjectScanner _projectScanner;
    private readonly IWorkflowRunner _workflowRunner;
    private readonly IBatchFileRenamer _batchFileRenamer;
    private readonly IExternalProcessRunner _processRunner;
    private readonly IDramaSearchService _dramaSearchService;
    private readonly IProjectInfoParser _projectInfoParser;

    public WorkService(
        IProjectScanner projectScanner,
        IWorkflowRunner workflowRunner,
        IBatchFileRenamer batchFileRenamer,
        IExternalProcessRunner processRunner,
        IDramaSearchService dramaSearchService,
        IProjectInfoParser projectInfoParser)
    {
        _projectScanner = projectScanner;
        _workflowRunner = workflowRunner;
        _batchFileRenamer = batchFileRenamer;
        _processRunner = processRunner;
        _dramaSearchService = dramaSearchService;
        _projectInfoParser = projectInfoParser;
    }

    public async Task<WorkRunResult> RunAsync(
        string rootDir,
        string? backupRootDir,
        bool force,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken)
    {
        var scan = await _projectScanner.ScanAsync(rootDir, backupRootDir, cancellationToken);
        var results = new List<ProjectWorkResult>();

        foreach (var project in scan.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!force &&
                string.Equals(project.Status, "已完成", StringComparison.Ordinal) &&
                ProjectOutputsAreComplete(project.WorkflowProjectDir))
            {
                progress?.Report(new WorkRunEvent(
                    project.ProjectKey,
                    project.DisplayName,
                    Kind: "project-skipped",
                    Message: "处理完成，跳过",
                    Ok: true));

                results.Add(new ProjectWorkResult(
                    project.ProjectKey,
                    project.DisplayName,
                    project.WorkflowProjectDir ?? string.Empty,
                    Ok: true,
                    Skipped: true,
                    Message: "项目已完成，跳过。",
                    Steps: []));
                continue;
            }

            results.Add(await RunProjectAsync(rootDir, scan.BackupRootDir, project, force, progress, cancellationToken, null));
        }

        return new WorkRunResult(
            RootDir: rootDir,
            BackupRootDir: scan.BackupRootDir,
            TotalProjects: scan.TotalProjects,
            SucceededProjects: results.Count(item => item.Ok && !item.Skipped),
            FailedProjects: results.Count(item => !item.Ok),
            SkippedProjects: results.Count(item => item.Skipped),
            Projects: results);
    }

    public async Task<ProjectWorkResult> RunProjectAsync(
        string sourceProjectDir,
        string? backupRootDir,
        bool force,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {sourceProjectDir}");
        }

        var rootDir = Directory.GetParent(sourceProjectDir)?.FullName
            ?? throw new InvalidOperationException($"无法确定项目根目录: {sourceProjectDir}");

        var scan = await _projectScanner.ScanAsync(rootDir, backupRootDir, cancellationToken);
        var project = scan.Projects.FirstOrDefault(item =>
            string.Equals(item.SourceProjectDir, sourceProjectDir, StringComparison.Ordinal));

        if (project is null)
        {
            throw new InvalidOperationException($"未在根目录下找到项目: {sourceProjectDir}");
        }

        if (!force &&
            string.Equals(project.Status, "已完成", StringComparison.Ordinal) &&
            ProjectOutputsAreComplete(project.WorkflowProjectDir))
        {
            progress?.Report(new WorkRunEvent(
                project.ProjectKey,
                project.DisplayName,
                Kind: "project-skipped",
                Message: "处理完成，跳过",
                Ok: true));

            return new ProjectWorkResult(
                project.ProjectKey,
                project.DisplayName,
                project.WorkflowProjectDir ?? string.Empty,
                Ok: true,
                Skipped: true,
                Message: "项目已完成，跳过。",
                Steps: []);
        }

        return await RunProjectAsync(rootDir, scan.BackupRootDir, project, force, progress, cancellationToken, null);
    }

    public async Task<ProjectWorkResult> RunProjectStepAsync(
        string sourceProjectDir,
        string? backupRootDir,
        string stepType,
        bool force,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken,
        string? configOverridePath = null)
    {
        if (string.IsNullOrWhiteSpace(stepType))
        {
            throw new InvalidOperationException("步骤类型不能为空。");
        }

        if (!Directory.Exists(sourceProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {sourceProjectDir}");
        }

        var rootDir = Directory.GetParent(sourceProjectDir)?.FullName
            ?? throw new InvalidOperationException($"无法确定项目根目录: {sourceProjectDir}");

        var scan = await _projectScanner.ScanAsync(rootDir, backupRootDir, cancellationToken);
        var project = scan.Projects.FirstOrDefault(item =>
            string.Equals(item.SourceProjectDir, sourceProjectDir, StringComparison.Ordinal));

        if (project is null)
        {
            throw new InvalidOperationException($"未在根目录下找到项目: {sourceProjectDir}");
        }

        return await RunProjectAsync(rootDir, scan.BackupRootDir, project, force, progress, cancellationToken, [NormalizeStepType(stepType)], configOverridePath);
    }

    public async Task<ProjectTitleUpdateResult> UpdateProjectTitleAsync(
        string sourceProjectDir,
        string? backupRootDir,
        string newTitle,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newTitle))
        {
            throw new InvalidOperationException("新剧名不能为空。");
        }

        if (!Directory.Exists(sourceProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {sourceProjectDir}");
        }

        var rootDir = Directory.GetParent(sourceProjectDir)?.FullName
            ?? throw new InvalidOperationException($"无法确定项目根目录: {sourceProjectDir}");

        var scan = await _projectScanner.ScanAsync(rootDir, backupRootDir, cancellationToken);
        var project = scan.Projects.FirstOrDefault(item =>
            string.Equals(item.SourceProjectDir, sourceProjectDir, StringComparison.Ordinal));

        if (project is null)
        {
            throw new InvalidOperationException($"未在根目录下找到项目: {sourceProjectDir}");
        }

        var sanitizedTitle = SanitizeDirectoryName(newTitle);
        if (string.IsNullOrWhiteSpace(sanitizedTitle))
        {
            throw new InvalidOperationException("新剧名无效。");
        }

        var originalTitle = project.DisplayName;
        if (string.Equals(originalTitle, sanitizedTitle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("新剧名未发生变化。");
        }

        var workflowRoot = Path.Combine(rootDir, "workflow");
        var conflictingDirs = new[]
        {
            Path.Combine(workflowRoot, sanitizedTitle),
            Path.Combine(workflowRoot, "_" + sanitizedTitle)
        };

        foreach (var candidate in conflictingDirs)
        {
            if (Directory.Exists(candidate) &&
                !string.Equals(project.WorkflowProjectDir, candidate, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"目标项目目录已存在: {candidate}");
            }
        }

        var context = await PrepareWorkspaceAsync(rootDir, scan.BackupRootDir, project, cancellationToken);
        var state = ReadState(context.WorkflowProjectDir);
        await EnsureProjectInfoAsync(project, context, cancellationToken);

        var infoPath = Path.Combine(context.WorkflowProjectDir, "短剧信息.txt");
        await UpdateProjectInfoTitleFileAsync(infoPath, sanitizedTitle, cancellationToken);

        var invalidatedSteps = new[]
        {
            "poster-rename",
            "project-image",
            "cost-report",
            "batch-file-rename",
            "material-convert",
            "weixin-upload",
            "weixin-material-upload"
        };

        foreach (var step in invalidatedSteps)
        {
            InvalidateStep(state, step);
        }

        var regeneratedSteps = new List<string>();
        var renamedVideoCount = 0;
        if (Directory.Exists(context.VideosOutputDir) &&
            Directory.EnumerateFiles(context.VideosOutputDir, "*.*", SearchOption.TopDirectoryOnly).Any(IsVideoFile))
        {
            var renameResult = await _batchFileRenamer.RenameAsync(
                new BatchFileRenameRequest(
                    ProjectDir: context.WorkflowProjectDir,
                    InputDir: context.VideosOutputDir,
                    ConfigFile: context.ConfigFile,
                    NameTemplate: null,
                    Overwrite: true),
                cancellationToken);

            renamedVideoCount = renameResult.RenamedCount;
            regeneratedSteps.Add("batch-file-rename");
            MarkCompleted(state, "batch-file-rename", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["renamedCount"] = renameResult.RenamedCount.ToString()
            });
        }

        foreach (var stepType in new[] { "poster-rename", "project-image", "cost-report", "material-convert" })
        {
            if (string.Equals(stepType, "cost-report", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(context.CostReportTemplatePath))
            {
                continue;
            }

            var definition = BuildDefinition(context, stepType, force: true);
            var result = await _workflowRunner.RunAsync(definition, progress: null, cancellationToken);
            var stepResult = result.Steps.Single();
            if (!stepResult.Ok)
            {
                throw new InvalidOperationException(stepResult.Message ?? $"步骤执行失败: {stepType}");
            }

            MarkCompleted(state, stepType, stepResult.Outputs);
            regeneratedSteps.Add(stepType);
        }

        var updatedWeixinConfigCount = NormalizeWeixinProjectConfigs(
            context.WorkflowProjectDir,
            sanitizedTitle);

        state.Completed = false;

        context = await ApplyRewriteTitleAsync(
            context,
            state,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = sanitizedTitle
            },
            progress: null,
            cancellationToken);

        await WriteStateAsync(context, state, cancellationToken);

        var remainingInvalidatedSteps = invalidatedSteps
            .Except(regeneratedSteps, StringComparer.Ordinal)
            .ToArray();

        return new ProjectTitleUpdateResult(
            project.ProjectKey,
            originalTitle,
            sanitizedTitle,
            context.WorkflowProjectDir,
            renamedVideoCount,
            updatedWeixinConfigCount,
            regeneratedSteps,
            remainingInvalidatedSteps);
    }

    public async Task<int> RefreshWeixinConfigsAsync(
        string sourceProjectDir,
        string? backupRootDir,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {sourceProjectDir}");
        }

        var rootDir = Directory.GetParent(sourceProjectDir)?.FullName
            ?? throw new InvalidOperationException($"无法确定项目根目录: {sourceProjectDir}");

        var scan = await _projectScanner.ScanAsync(rootDir, backupRootDir, cancellationToken);
        var project = scan.Projects.FirstOrDefault(item =>
            string.Equals(item.SourceProjectDir, sourceProjectDir, StringComparison.Ordinal));

        if (project is null)
        {
            throw new InvalidOperationException($"未在根目录下找到项目: {sourceProjectDir}");
        }

        var context = await PrepareWorkspaceAsync(rootDir, scan.BackupRootDir, project, cancellationToken);
        await EnsureProjectInfoAsync(project, context, cancellationToken);
        var info = await _projectInfoParser.ParseAsync(context.WorkflowProjectDir, cancellationToken);
        return NormalizeWeixinProjectConfigs(context.WorkflowProjectDir, info.Title);
    }

    public async Task<string> EnsureWeixinUploadConfigAsync(
        string sourceProjectDir,
        string? backupRootDir,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {sourceProjectDir}");
        }

        var rootDir = Directory.GetParent(sourceProjectDir)?.FullName
            ?? throw new InvalidOperationException($"无法确定项目根目录: {sourceProjectDir}");

        var scan = await _projectScanner.ScanAsync(rootDir, backupRootDir, cancellationToken);
        var project = scan.Projects.FirstOrDefault(item =>
            string.Equals(item.SourceProjectDir, sourceProjectDir, StringComparison.Ordinal));

        if (project is null)
        {
            throw new InvalidOperationException($"未在根目录下找到项目: {sourceProjectDir}");
        }

        var context = await PrepareWorkspaceAsync(rootDir, scan.BackupRootDir, project, cancellationToken);
        await EnsureProjectInfoAsync(project, context, cancellationToken);
        var info = await _projectInfoParser.ParseAsync(context.WorkflowProjectDir, cancellationToken);

        var configPath = ResolveWeixinConfigPath(context.WorkflowProjectDir);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            configPath = Path.Combine(context.WorkflowProjectDir, "weixin-channel-autogen.json");
            await File.WriteAllTextAsync(
                configPath,
                BuildWeixinUploadConfigJson(context, info),
                cancellationToken);
        }
        else
        {
            NormalizeWeixinProjectConfigs(context.WorkflowProjectDir, info.Title);
        }

        return configPath;
    }

    private async Task<ProjectWorkResult> RunProjectAsync(
        string rootDir,
        string? backupRootDir,
        ScannedProject project,
        bool force,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? stepTypes,
        string? configOverridePath = null)
    {
        var context = await PrepareWorkspaceAsync(rootDir, backupRootDir, project, cancellationToken);
        var state = ReadState(context.WorkflowProjectDir);
        var stepResults = new List<WorkflowStepResult>();
        var stepsToRun = stepTypes is { Count: > 0 }
            ? stepTypes.Select(NormalizeStepType).ToArray()
            : DefaultStepTypes;
        string? currentStepType = null;

        try
        {
            foreach (var stepType in stepsToRun)
            {
                currentStepType = stepType;
                cancellationToken.ThrowIfCancellationRequested();

                if (!force &&
                    string.Equals(stepType, "download", StringComparison.Ordinal) &&
                    HasCompleteDownloadedVideos(context))
                {
                    progress?.Report(new WorkRunEvent(
                        project.ProjectKey,
                        context.DisplayName,
                        Kind: "step-skipped",
                        StepType: stepType,
                        Message: "源目录已存在完整下载结果，跳过下载",
                        Ok: true));

                    MarkCompleted(state, stepType, BuildDownloadOutputs(context));
                    state.Completed = AreAllDefaultStepsCompleted(state);
                    context = await NormalizeWorkflowDirectoryByProjectMaterialStateAsync(context, state, progress, cancellationToken);
                    await WriteStateAsync(context, state, cancellationToken);
                    stepResults.Add(new WorkflowStepResult(stepType, true, Message: "源目录已存在完整下载结果，跳过下载"));
                    continue;
                }

                if (!force &&
                    string.Equals(stepType, "transcode", StringComparison.Ordinal) &&
                    HasCompleteTranscodedVideos(context))
                {
                    progress?.Report(new WorkRunEvent(
                        project.ProjectKey,
                        context.DisplayName,
                        Kind: "step-skipped",
                        StepType: stepType,
                        Message: "videos 目录已存在完整转码结果，跳过转码",
                        Ok: true));

                    MarkCompleted(state, stepType, BuildTranscodeOutputs(context));
                    state.Completed = AreAllDefaultStepsCompleted(state);
                    context = await NormalizeWorkflowDirectoryByProjectMaterialStateAsync(context, state, progress, cancellationToken);
                    await WriteStateAsync(context, state, cancellationToken);
                    stepResults.Add(new WorkflowStepResult(stepType, true, Message: "videos 目录已存在完整转码结果，跳过转码"));
                    continue;
                }

                var completedStep = state.GetStep(stepType);
                if (!force && completedStep?.IsCompleted == true)
                {
                    if (!HasExpectedOutputs(context, stepType, completedStep))
                    {
                        progress?.Report(new WorkRunEvent(
                            project.ProjectKey,
                            context.DisplayName,
                            Kind: "step-retry",
                            StepType: stepType,
                            Message: "产物缺失，重新生成",
                            Ok: true));
                    }
                    else
                    {
                    progress?.Report(new WorkRunEvent(
                        project.ProjectKey,
                        context.DisplayName,
                        Kind: "step-skipped",
                        StepType: stepType,
                        Message: "任务已完成，跳过",
                        Ok: true));

                    stepResults.Add(new WorkflowStepResult(stepType, true, Message: "任务已完成，跳过"));
                    continue;
                    }
                }

                progress?.Report(new WorkRunEvent(
                    project.ProjectKey,
                    context.DisplayName,
                    Kind: "step-started",
                    StepType: stepType,
                    Message: "开始任务...",
                    Ok: null));

                if (StepRequiresProjectInfo(stepType))
                {
                    var infoPrepared = await EnsureProjectInfoAsync(project, context, cancellationToken);
                    if (infoPrepared)
                    {
                        InvalidateStep(state, "batch-file-rename");
                        progress?.Report(new WorkRunEvent(
                            project.ProjectKey,
                            context.DisplayName,
                            Kind: "step-output",
                            StepType: stepType,
                            Message: "已生成短剧信息.txt 供后续步骤使用",
                            Ok: true));
                    }
                }

                var definition = BuildDefinition(context, stepType, force, configOverridePath);
                var workflowProgress = progress is null
                    ? null
                    : new InlineProgress<WorkflowRuntimeEvent>(evt =>
                        progress.Report(new WorkRunEvent(
                            project.ProjectKey,
                            context.DisplayName,
                            Kind: evt.Kind,
                            StepType: evt.StepType,
                            Message: evt.Message,
                            Ok: null)));

                var result = await _workflowRunner.RunAsync(definition, workflowProgress, cancellationToken);
                var stepResult = result.Steps.Single();
                stepResults.Add(stepResult);

                if (stepResult.Ok)
                {
                    progress?.Report(new WorkRunEvent(
                        project.ProjectKey,
                        context.DisplayName,
                        Kind: "step-completed",
                        StepType: stepType,
                        Message: stepResult.Message ?? "任务完成",
                        Ok: true));

                    MarkCompleted(state, stepType, stepResult.Outputs);
                    if (string.Equals(stepType, "rewrite", StringComparison.Ordinal))
                    {
                        InvalidateStep(state, "batch-file-rename");
                        context = await ApplyRewriteTitleAsync(
                            context,
                            state,
                            stepResult.Outputs,
                            progress,
                            cancellationToken);
                    }

                    state.Completed = AreAllDefaultStepsCompleted(state);
                    context = await NormalizeWorkflowDirectoryByProjectMaterialStateAsync(context, state, progress, cancellationToken);
                    await WriteStateAsync(context, state, cancellationToken);
                    continue;
                }

                if (string.Equals(stepResult.ErrorCode, "STEP_PENDING", StringComparison.Ordinal))
                {
                    progress?.Report(new WorkRunEvent(
                        project.ProjectKey,
                        context.DisplayName,
                        Kind: "step-deferred",
                        StepType: stepType,
                        Message: stepResult.Message ?? "存在未完成文件，可继续运行",
                        Ok: true));

                    MarkFailed(state, stepType, stepResult.Message ?? "存在未完成文件，可继续运行");
                    state.Completed = false;
                    await WriteStateAsync(context, state, cancellationToken);
                    break;
                }

                progress?.Report(new WorkRunEvent(
                    project.ProjectKey,
                    context.DisplayName,
                    Kind: "step-failed",
                    StepType: stepType,
                    Message: stepResult.Message ?? "任务失败",
                    Ok: false));

                MarkFailed(state, stepType, stepResult.Message);
                state.Completed = AreAllDefaultStepsCompleted(state);
                await WriteStateAsync(context, state, cancellationToken);

                progress?.Report(new WorkRunEvent(
                    project.ProjectKey,
                    context.DisplayName,
                    Kind: "project-failed",
                    Message: stepResult.Message ?? "处理失败",
                    Ok: false));

                return new ProjectWorkResult(
                    project.ProjectKey,
                    context.DisplayName,
                    context.WorkflowProjectDir,
                    Ok: false,
                    Skipped: false,
                    Message: stepResult.Message ?? "项目处理失败。",
                    Steps: stepResults);
            }
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(currentStepType))
            {
                MarkFailed(state, currentStepType, "已停止，可继续运行");
                state.Completed = false;
                await WriteStateAsync(context, state, CancellationToken.None);

                progress?.Report(new WorkRunEvent(
                    project.ProjectKey,
                    context.DisplayName,
                    Kind: "step-cancelled",
                    StepType: currentStepType,
                    Message: "已停止，可继续运行",
                    Ok: false));
            }

            progress?.Report(new WorkRunEvent(
                project.ProjectKey,
                context.DisplayName,
                Kind: "project-cancelled",
                Message: "当前项目已停止，进度已保存",
                Ok: false));

            throw;
        }

        state.Completed = AreAllDefaultStepsCompleted(state);
        if (!state.Completed)
        {
            await WriteStateAsync(context, state, cancellationToken);
            progress?.Report(new WorkRunEvent(
                project.ProjectKey,
                context.DisplayName,
                Kind: "project-partial-completed",
                Message: "步骤执行完成",
                Ok: true));

            return new ProjectWorkResult(
                project.ProjectKey,
                context.DisplayName,
                context.WorkflowProjectDir,
                Ok: true,
                Skipped: false,
                Message: "步骤执行完成",
                Steps: stepResults);
        }

        var finalDir = FinalizeWorkflowDirectory(context);
        if (!string.Equals(context.WorkflowProjectDir, finalDir, StringComparison.Ordinal))
        {
            RewriteStatePaths(state, context.WorkflowProjectDir, finalDir);
        }
        state.Completed = true;
        state.WorkflowProjectDir = finalDir;
        await WriteStateAsync(context with { WorkflowProjectDir = finalDir }, state, cancellationToken);

        progress?.Report(new WorkRunEvent(
            project.ProjectKey,
            context.DisplayName,
            Kind: "project-completed",
            Message: "处理完成",
            Ok: true));

        return new ProjectWorkResult(
            project.ProjectKey,
            context.DisplayName,
            finalDir,
            Ok: true,
            Skipped: false,
            Message: "处理完成",
            Steps: stepResults);
    }

    private async Task<ProjectWorkspaceContext> PrepareWorkspaceAsync(
        string rootDir,
        string? backupRootDir,
        ScannedProject project,
        CancellationToken cancellationToken)
    {
        var workflowRoot = Path.Combine(rootDir, "workflow");
        Directory.CreateDirectory(workflowRoot);

        var workflowDir = project.WorkflowProjectDir;
        if (string.IsNullOrWhiteSpace(workflowDir))
        {
            workflowDir = Path.Combine(workflowRoot, $"_{project.DisplayName}");
            Directory.CreateDirectory(workflowDir);
        }
        else
        {
            Directory.CreateDirectory(workflowDir);
        }

        var videosDir = Path.Combine(workflowDir, "videos");
        Directory.CreateDirectory(videosDir);
        var materialVideosDir = Path.Combine(workflowDir, "material-videos");

        var configDir = Path.Combine(rootDir, "config");
        var configFile = Path.Combine(configDir, "config.txt");
        var companyName = File.Exists(configFile)
            ? KeyValueConfigReader.Read(configFile).TryGetValue("CompanyName", out var company) ? company : "未填写公司"
            : "未填写公司";

        var configMap = File.Exists(configFile) ? KeyValueConfigReader.Read(configFile) : null;
        var templatePath = ResolveTemplatePath(configDir, configMap);
        var projectImageTemplateDir = ResolveProjectImageTemplateDir(configDir, configMap);

        return new ProjectWorkspaceContext(
            project.ProjectKey,
            project.DisplayName,
            project.SourceProjectDir,
            project.BackupProjectDir,
            workflowDir,
            videosDir,
            materialVideosDir,
            backupRootDir,
            configDir,
            File.Exists(configFile) ? configFile : null,
            companyName,
            templatePath,
            projectImageTemplateDir);
    }

    private async Task<bool> EnsureProjectInfoAsync(
        ScannedProject project,
        ProjectWorkspaceContext context,
        CancellationToken cancellationToken)
    {
        var workflowDir = context.WorkflowProjectDir;
        var workflowInfoPath = Path.Combine(workflowDir, "短剧信息.txt");
        if (File.Exists(workflowInfoPath))
        {
            return false;
        }

        var candidates = new[]
        {
            Path.Combine(project.SourceProjectDir, "短剧信息.txt"),
            project.BackupProjectDir is null ? null : Path.Combine(project.BackupProjectDir, "短剧信息.txt"),
            project.WorkflowProjectDir is null ? null : Path.Combine(project.WorkflowProjectDir, "短剧信息.txt")
        }.Where(path => path is not null && File.Exists(path)).Cast<string>().ToList();

        if (candidates.Count > 0)
        {
            File.Copy(candidates[0], workflowInfoPath, overwrite: true);
            return true;
        }

        var metadata = ProjectAutomationMetadata.Resolve(project.SourceProjectDir);
        var videoFiles = Directory.EnumerateFiles(project.SourceProjectDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => IsVideoFile(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalSeconds = 0d;
        foreach (var file in videoFiles)
        {
            totalSeconds += await TryProbeDurationAsync(file, cancellationToken);
        }

        var totalMinutes = Math.Max(1, (int)Math.Ceiling(totalSeconds / 60d));
        var episodeCount = Math.Max(1, videoFiles.Count);
        var title = string.IsNullOrWhiteSpace(metadata.Title) ? project.DisplayName : metadata.Title;
        var sourceName = string.IsNullOrWhiteSpace(metadata.SourceName) ? project.SourceName : metadata.SourceName;
        var synopsis = await ResolveProjectSynopsisAsync(metadata, sourceName, cancellationToken);
        var costAmountWan = CalculateCostAmountWan(totalMinutes);

        await File.WriteAllTextAsync(workflowInfoPath, $$"""
原剧名: {{sourceName}}
新剧名: {{title}}
推荐语: 
简介: {{synopsis}}
短标题: 
标签: 
时长: {{totalMinutes}} 分钟
集数: {{episodeCount}}
成本: {{costAmountWan:0}} 万元
制作公司: {{context.CompanyName}}
""", cancellationToken);
        return true;
    }

    private async Task<string> ResolveProjectSynopsisAsync(
        ProjectAutomationMetadata metadata,
        string sourceName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Intro))
        {
            return metadata.Intro.Trim();
        }

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return string.Empty;
        }

        try
        {
            var results = await _dramaSearchService.SearchAsync(sourceName, 1, cancellationToken);
            var matched = results.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(metadata.BookId) &&
                 string.Equals(item.BookId, metadata.BookId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(item.Title?.Trim(), sourceName.Trim(), StringComparison.Ordinal));

            return matched?.Intro?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static decimal CalculateCostAmountWan(int totalMinutes)
    {
        var minutes = Math.Max(1, totalMinutes);
        var totalYuan = decimal.Round(minutes * CostPerMinuteYuan, 0, MidpointRounding.AwayFromZero);
        return decimal.Round(totalYuan / 10000m, 0, MidpointRounding.AwayFromZero);
    }

    private async Task<double> TryProbeDurationAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                ResolveFfprobeBinary(),
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", videoPath],
                Path.GetDirectoryName(videoPath),
                cancellationToken);

            if (result.ExitCode != 0)
            {
                return 0d;
            }

            return double.TryParse(result.StandardOutput.Trim(), out var duration) ? duration : 0d;
        }
        catch
        {
            return 0d;
        }
    }

    private static string ResolveFfprobeBinary()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            throw new InvalidOperationException("未找到 ffprobe。");
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fullPath = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new InvalidOperationException("未找到 ffprobe。");
    }

    private static bool IsVideoFile(string path)
    {
        return new[] { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsImageFile(string path)
    {
        return new[] { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static string? ResolvePosterSourcePath(ProjectWorkspaceContext context)
    {
        var sourceCandidates = new[]
        {
            context.SourceProjectDir,
            context.BackupProjectDir
        };

        foreach (var dir in sourceCandidates)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            var preferred = ResolvePosterCandidate(dir);

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }
        }

        return null;
    }

    private static string? ResolvePosterCandidate(string directory)
    {
        var direct = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsImageFile)
            .Where(path =>
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                return !fileName.StartsWith("工程图_", StringComparison.Ordinal) &&
                       !fileName.StartsWith("成本报表", StringComparison.Ordinal) &&
                       !fileName.StartsWith("seal.prepared", StringComparison.Ordinal) &&
                       !string.Equals(fileName, "seal", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(fileName, "sign", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var tempDir = Path.Combine(directory, "temp");
        if (!Directory.Exists(tempDir))
        {
            return null;
        }

        var thumbnail = Directory.EnumerateFiles(tempDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsImageFile)
            .Where(path => Path.GetFileName(path).Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(thumbnail))
        {
            return thumbnail;
        }

        return Directory.EnumerateFiles(tempDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsImageFile)
            .Where(path => Path.GetFileName(path).Contains("frame_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool ProjectOutputsAreComplete(string? workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir) || !Directory.Exists(workflowProjectDir))
        {
            return false;
        }

        var hasInfo = File.Exists(Path.Combine(workflowProjectDir, "短剧信息.txt"));
        var hasPoster = File.Exists(Path.Combine(workflowProjectDir, "海报图片.jpg"));
        var hasProjectImages = Directory.EnumerateFiles(workflowProjectDir, "工程图_*.png", SearchOption.TopDirectoryOnly).Any();
        var hasCost = File.Exists(Path.Combine(workflowProjectDir, "成本报表.png"));
        var videosDir = Path.Combine(workflowProjectDir, "videos");
        var hasVideos = Directory.Exists(videosDir) &&
            Directory.EnumerateFiles(videosDir, "*.*", SearchOption.TopDirectoryOnly).Any(IsVideoFile);
        return hasInfo && hasPoster && hasProjectImages && hasCost && hasVideos;
    }

    private static bool AreAllDefaultStepsCompleted(WorkStateFile state)
    {
        return DefaultStepTypes.All(state.IsCompleted);
    }

    private static bool AreProjectMaterialStepsCompleted(WorkStateFile state)
    {
        return ProjectMaterialStepTypes.All(state.IsCompleted);
    }

    private static bool StepRequiresProjectInfo(string stepType)
    {
        return NormalizeStepType(stepType) switch
        {
            "rewrite" => true,
            "poster-rename" => true,
            "project-image" => true,
            "cost-report" => true,
            "batch-file-rename" => true,
            _ => false
        };
    }

    private static bool HasExpectedOutputs(ProjectWorkspaceContext context, string stepType, StateStep step)
    {
        bool Exists(string key) =>
            step.Outputs.TryGetValue(key, out var path) &&
            !string.IsNullOrWhiteSpace(path) &&
            File.Exists(path);

        bool AnyFile(string prefix) =>
            step.Outputs.Any(item =>
                item.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.Value) &&
                File.Exists(item.Value));

        return NormalizeStepType(stepType) switch
        {
            "download" => HasCompleteDownloadedVideos(context),
            "rewrite" => Exists("outputFile"),
            "poster-rename" => Exists("outputFile"),
            "project-image" => AnyFile("image"),
            "cost-report" => Exists("png"),
            "batch-file-rename" => AnyFile("file"),
            "transcode" => HasCompleteTranscodedVideos(context),
            "material-convert" => HasCompleteMaterialConvertedVideos(context),
            "weixin-upload" => true,
            _ => true
        };
    }

    private static bool HasCompleteTranscodedVideos(ProjectWorkspaceContext context)
    {
        var sourceVideoCount = CountSourceVideos(context);
        if (sourceVideoCount <= 0)
        {
            return false;
        }

        return CountTranscodedVideos(context) >= sourceVideoCount;
    }

    private static bool HasCompleteDownloadedVideos(ProjectWorkspaceContext context)
    {
        return LocalProjectDownloadInspector.Inspect(context.SourceProjectDir).IsComplete;
    }

    private static bool HasCompleteMaterialConvertedVideos(ProjectWorkspaceContext context)
    {
        var transcodedCount = CountTranscodedVideos(context);
        if (transcodedCount <= 0)
        {
            return false;
        }

        return CountMaterialConvertedVideos(context) >= transcodedCount;
    }

    private static int CountSourceVideos(ProjectWorkspaceContext context)
    {
        var candidateDirs = new[]
        {
            context.SourceProjectDir,
            context.BackupProjectDir
        };

        foreach (var dir in candidateDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            var count = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(IsVideoFile);
            if (count > 0)
            {
                return count;
            }
        }

        return 0;
    }

    private static int CountTranscodedVideos(ProjectWorkspaceContext context)
    {
        if (!Directory.Exists(context.VideosOutputDir))
        {
            return 0;
        }

        return Directory.EnumerateFiles(context.VideosOutputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Count(IsVideoFile);
    }

    private static int CountMaterialConvertedVideos(ProjectWorkspaceContext context)
    {
        if (!Directory.Exists(context.MaterialVideosOutputDir))
        {
            return 0;
        }

        return Directory.EnumerateFiles(context.MaterialVideosOutputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Count(IsVideoFile);
    }

    private static IReadOnlyDictionary<string, string> BuildDownloadOutputs(ProjectWorkspaceContext context)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(context.SourceProjectDir))
        {
            return outputs;
        }

        var files = Directory.EnumerateFiles(context.SourceProjectDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsVideoFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < files.Length; index++)
        {
            outputs[$"video{index + 1}"] = files[index];
        }

        outputs["videoCount"] = files.Length.ToString();
        return outputs;
    }

    private static IReadOnlyDictionary<string, string> BuildTranscodeOutputs(ProjectWorkspaceContext context)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(context.VideosOutputDir))
        {
            return outputs;
        }

        var files = Directory.EnumerateFiles(context.VideosOutputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsVideoFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < files.Length; index++)
        {
            outputs[$"video{index + 1}"] = files[index];
        }

        return outputs;
    }

    private static string? ResolveTemplatePath(string configDir, IReadOnlyDictionary<string, string>? configMap)
    {
        if (configMap is not null)
        {
            foreach (var key in new[] { "TemplateDocxPath", "CostReportTemplatePath" })
            {
                if (configMap.TryGetValue(key, out var configured) && !string.IsNullOrWhiteSpace(configured))
                {
                    return Path.IsPathRooted(configured)
                        ? configured
                        : Path.GetFullPath(Path.Combine(configDir, configured));
                }
            }
        }

        var defaultPath = Path.Combine(configDir, "1111.docx");
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private static async Task UpdateProjectInfoTitleFileAsync(
        string filePath,
        string newTitle,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"未找到项目说明文件: {filePath}", filePath);
        }

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        var updated = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("新剧名:", StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = $"新剧名: {newTitle}";
            updated = true;
            break;
        }

        if (!updated)
        {
            var mutable = lines.ToList();
            var originalTitleIndex = mutable.FindIndex(line => line.StartsWith("原剧名:", StringComparison.Ordinal));
            if (originalTitleIndex >= 0)
            {
                mutable.Insert(originalTitleIndex + 1, $"新剧名: {newTitle}");
            }
            else
            {
                mutable.Insert(0, $"新剧名: {newTitle}");
            }

            lines = mutable.ToArray();
        }

        await File.WriteAllLinesAsync(filePath, lines, cancellationToken);
    }

    private static int NormalizeWeixinProjectConfigs(
        string workflowProjectDir,
        string title)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir) || !Directory.Exists(workflowProjectDir))
        {
            return 0;
        }

        var updatedCount = 0;
        foreach (var configPath in EnumerateWeixinProjectConfigPaths(workflowProjectDir))
        {
            if (UpdateWeixinProjectConfig(configPath, workflowProjectDir, title))
            {
                updatedCount++;
            }
        }

        return updatedCount;
    }

    private static string BuildWeixinUploadConfigJson(ProjectWorkspaceContext context, ProjectInfo info)
    {
        var configMap = string.IsNullOrWhiteSpace(context.ConfigFile) || !File.Exists(context.ConfigFile)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : KeyValueConfigReader.Read(context.ConfigFile);

        var outputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".weixin_channel_tool",
            "output",
            $"weixin-channel-submit-{info.Title}");
        var projectImages = Directory.EnumerateFiles(context.WorkflowProjectDir, "工程图_*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProofMaterialImageCount)
            .ToArray();
        var videos = EnumerateWorkflowVideos(context.WorkflowProjectDir).ToArray();
        var trialEpisodes = configMap.TryGetValue("WeixinTrialEpisodes", out var trialRaw) && int.TryParse(trialRaw, out var trialParsed) && trialParsed > 0
            ? Math.Min(trialParsed, Math.Max(1, info.EpisodeCount))
            : 3;

        var root = new JsonObject
        {
            ["base_url"] = "https://channels.weixin.qq.com",
            ["auth_file"] = WeixinRuntimePaths.DefaultAuthFilePath,
            ["output_dir"] = outputDir,
            ["browser"] = new JsonObject
            {
                ["headless"] = false,
                ["slow_mo_ms"] = 50,
                ["keep_open_seconds"] = 0
            },
            ["login"] = new JsonObject
            {
                ["timeout_seconds"] = 300
            },
            ["debug"] = new JsonObject
            {
                ["log_file"] = Path.Combine(outputDir, "run.log"),
                ["save_html"] = true,
                ["save_text"] = true
            },
            ["navigation"] = new JsonObject
            {
                ["section"] = "收入与服务",
                ["item"] = "剧集管理",
                ["entry_button"] = "上架剧集"
            },
            ["first_page"] = new JsonObject
            {
                ["next_button_text"] = "下一步",
                ["actions"] = new JsonArray
                {
                    BuildFillAction("剧目名称", info.Title),
                    BuildFillAction("剧目简介", info.Synopsis ?? string.Empty, control: "textarea"),
                    BuildFillAction("推荐语", info.Tagline ?? string.Empty),
                    BuildFillAction("总集数", info.EpisodeCount.ToString()),
                    BuildChooseAction("变现类型", ResolveConfigString(configMap, "WeixinMonetizationType") ?? "IAA广告变现"),
                    BuildFillAction("试看集数", trialEpisodes.ToString()),
                    BuildChooseAction("剧目类型", ResolveConfigString(configMap, "WeixinDramaType") ?? "漫剧"),
                    BuildSetCheckedAction("AI内容声明", true),
                    BuildUploadAction("剧目海报", [Path.Combine(context.WorkflowProjectDir, "海报图片.jpg")], "选择图片"),
                    BuildUploadAction("推广海报", [Path.Combine(context.WorkflowProjectDir, "海报图片.jpg")], "选择图片"),
                    BuildChooseAction("提审身份", ResolveConfigString(configMap, "WeixinSubmitterIdentity") ?? "剧目制作方"),
                    BuildFillAction("制作方名称", info.CompanyName),
                    BuildUploadAction("剧目制作证明材料", projectImages, "选择文件"),
                    BuildChooseAction("剧目资质", ResolveConfigString(configMap, "WeixinDramaQualification") ?? "其他微短剧"),
                    new JsonObject
                    {
                        ["type"] = "fill",
                        ["selector"] = "input[placeholder*='制作成本'], input[placeholder*='成本']",
                        ["value"] = $"{info.CostAmountWan:0.####}"
                    },
                    BuildUploadAction("剧目资质", [Path.Combine(context.WorkflowProjectDir, "成本报表.png")], null),
                    BuildSetCheckedAction("我已知悉并同意", true),
                    new JsonObject
                    {
                        ["type"] = "screenshot",
                        ["name"] = "first-page-filled",
                        ["message"] = $"{info.Title}第一页已填完。"
                    }
                }
            },
            ["second_page"] = new JsonObject
            {
                ["ready_text"] = "请选择要上传的视频文件",
                ["actions_before_upload"] = new JsonArray(),
                ["upload"] = new JsonObject
                {
                    ["input_selector"] = ".weui-desktop-form__control-group_label-r:has(.weui-desktop-form__label:has-text(\"选取视频\")) input[type=file]",
                    ["paths"] = new JsonArray(videos.Select(path => JsonValue.Create(path)).ToArray()),
                    ["timeout_seconds"] = 3600,
                    ["success_texts"] = new JsonArray("已上传成功", "上传成功", "处理完成"),
                    ["error_texts"] = new JsonArray("上传失败", "未能上传", "上传异常", "不符合要求", "格式不支持", "超出限制")
                },
                ["enter_submit_page"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["text"] = "确认提审"
                },
                ["actions_after_upload"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "screenshot",
                        ["name"] = "second-page-uploaded",
                        ["message"] = $"{info.Title}第二页视频已上传完成。"
                    }
                }
            },
            ["submit"] = new JsonObject
            {
                ["enabled"] = false,
                ["text"] = "确认提审"
            }
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildFillAction(string label, string value, string? control = null)
    {
        var action = new JsonObject
        {
            ["type"] = "fill",
            ["label"] = label,
            ["value"] = value
        };
        if (!string.IsNullOrWhiteSpace(control))
        {
            action["control"] = control;
        }

        return action;
    }

    private static JsonObject BuildChooseAction(string fieldLabel, string optionText)
    {
        return new JsonObject
        {
            ["type"] = "choose",
            ["field_label"] = fieldLabel,
            ["option_text"] = optionText
        };
    }

    private static JsonObject BuildSetCheckedAction(string label, bool enabled)
    {
        return new JsonObject
        {
            ["type"] = "set_checked",
            ["label"] = label,
            ["enabled"] = enabled
        };
    }

    private static JsonObject BuildUploadAction(string label, IEnumerable<string> paths, string? text)
    {
        var action = new JsonObject
        {
            ["type"] = "upload",
            ["label"] = label,
            ["paths"] = new JsonArray(paths.Where(File.Exists).Select(path => JsonValue.Create(path)).ToArray())
        };
        if (!string.IsNullOrWhiteSpace(text))
        {
            action["text"] = text;
        }

        return action;
    }

    private static string? ResolveConfigString(IReadOnlyDictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static IEnumerable<string> EnumerateWeixinProjectConfigPaths(string workflowDir)
    {
        foreach (var fileName in WeixinConfigFileNames)
        {
            var candidate = Path.Combine(workflowDir, fileName);
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool UpdateWeixinProjectConfig(
        string configPath,
        string workflowProjectDir,
        string title)
    {
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(File.ReadAllText(configPath));
        }
        catch
        {
            return false;
        }

        if (rootNode is not JsonObject root)
        {
            return false;
        }

        var changed = false;
        var outputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".weixin_channel_tool",
            "output",
            $"weixin-channel-submit-{title}");
        root["output_dir"] = outputDir;
        changed = true;

        if (root["debug"] is JsonObject debug)
        {
            debug["log_file"] = Path.Combine(outputDir, "run.log");
            changed = true;
        }

        if (root["first_page"] is JsonObject firstPage &&
            firstPage["actions"] is JsonArray firstPageActions)
        {
            changed |= UpdateFirstPageActions(firstPageActions, workflowProjectDir, title);
        }

        if (root["second_page"] is JsonObject secondPage &&
            secondPage["upload"] is JsonObject upload)
        {
            upload["paths"] = new JsonArray(EnumerateWorkflowVideos(workflowProjectDir)
                .Select(path => JsonValue.Create(path))
                .ToArray());
            changed = true;
        }

        if (root["second_page"] is JsonObject secondPageRoot &&
            secondPageRoot["actions_after_upload"] is JsonArray afterUpload)
        {
            foreach (var item in afterUpload.OfType<JsonObject>())
            {
                if (item["message"] is not null)
                {
                    item["message"] = $"{title}第二页视频已上传完成。";
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    private static bool UpdateFirstPageActions(JsonArray actions, string workflowProjectDir, string title)
    {
        var changed = false;
        var posterPath = Path.Combine(workflowProjectDir, "海报图片.jpg");
        var costPath = Path.Combine(workflowProjectDir, "成本报表.png");
        var projectImages = Directory.EnumerateFiles(workflowProjectDir, "工程图_*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProofMaterialImageCount)
            .ToArray();

        foreach (var node in actions)
        {
            if (node is not JsonObject action)
            {
                continue;
            }

            var label = action["label"]?.GetValue<string>();
            var type = action["type"]?.GetValue<string>();
            if (string.Equals(type, "fill", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(label, "剧目名称", StringComparison.Ordinal))
            {
                action["value"] = title;
                changed = true;
            }

            if (!string.Equals(type, "upload", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (label)
            {
                case "剧目海报":
                case "推广海报":
                    if (File.Exists(posterPath))
                    {
                        action["paths"] = new JsonArray(JsonValue.Create(posterPath));
                        changed = true;
                    }
                    break;
                case "剧目制作证明材料":
                    if (projectImages.Length > 0)
                    {
                        action["paths"] = new JsonArray(projectImages.Select(path => JsonValue.Create(path)).ToArray());
                        changed = true;
                    }
                    break;
                case "剧目资质":
                    if (File.Exists(costPath))
                    {
                        action["paths"] = new JsonArray(JsonValue.Create(costPath));
                        changed = true;
                    }
                    break;
            }
        }

        return changed;
    }

    private static IEnumerable<string> EnumerateWorkflowVideos(string workflowProjectDir)
    {
        var videosDir = Path.Combine(workflowProjectDir, "videos");
        if (!Directory.Exists(videosDir))
        {
            return [];
        }

        return Directory.EnumerateFiles(videosDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsVideoFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ResolveProjectImageTemplateDir(string configDir, IReadOnlyDictionary<string, string>? configMap)
    {
        if (configMap is null ||
            !configMap.TryGetValue("ProjectImageTemplateDir", out var configured) ||
            string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(configDir, configured));
    }

    private static WorkflowDefinition BuildDefinition(ProjectWorkspaceContext context, string stepType, bool force, string? configOverridePath = null)
    {
        WorkflowStep step = stepType switch
        {
            "download" => new(
                Type: "download",
                Template: null,
                ConfigFile: context.ConfigFile,
                OutputFile: null,
                InputDir: null,
                OutputDir: context.SourceProjectDir,
                NameTemplate: null,
                Overwrite: force),
            "transcode" => new(
                Type: "transcode",
                Template: null,
                ConfigFile: context.ConfigFile,
                OutputFile: null,
                InputDir: context.SourceProjectDir,
                OutputDir: context.VideosOutputDir,
                NameTemplate: null,
                Overwrite: force),
            "rewrite" => new(
                Type: "rewrite",
                Template: null,
                ConfigFile: context.ConfigFile,
                OutputFile: Path.Combine(context.WorkflowProjectDir, "短剧信息.txt"),
                InputDir: null,
                OutputDir: null,
                NameTemplate: null,
                Overwrite: true),
            "poster-rename" => new(
                Type: "poster-rename",
                Template: null,
                ConfigFile: context.ConfigFile,
                OutputFile: Path.Combine(context.WorkflowProjectDir, "海报图片.jpg"),
                InputDir: ResolvePosterSourcePath(context),
                OutputDir: null,
                NameTemplate: "海报图片",
                Overwrite: true),
            "project-image" => new(
                Type: "project-image",
                Template: context.ProjectImageTemplateDir,
                ConfigFile: context.ConfigFile,
                OutputFile: null,
                InputDir: context.VideosOutputDir,
                OutputDir: context.WorkflowProjectDir,
                NameTemplate: null,
                Overwrite: true),
            "cost-report" => new(
                Type: "cost-report",
                Template: context.CostReportTemplatePath,
                ConfigFile: context.ConfigFile,
                OutputFile: null,
                InputDir: null,
                OutputDir: context.WorkflowProjectDir,
                NameTemplate: null,
                Overwrite: true),
            "batch-file-rename" => new(
                Type: "batch-file-rename",
                Template: null,
                ConfigFile: context.ConfigFile,
                OutputFile: null,
                InputDir: context.VideosOutputDir,
                OutputDir: null,
                NameTemplate: null,
                Overwrite: true),
            "material-convert" => new(
                Type: "material-convert",
                Template: null,
                ConfigFile: context.ConfigFile,
                OutputFile: null,
                InputDir: context.VideosOutputDir,
                OutputDir: context.MaterialVideosOutputDir,
                NameTemplate: null,
                Overwrite: force),
            "weixin-upload" => new(
                Type: "weixin-upload",
                Template: null,
                ConfigFile: configOverridePath ?? ResolveWeixinConfigPath(context.WorkflowProjectDir),
                OutputFile: null,
                InputDir: null,
                OutputDir: context.WorkflowProjectDir,
                NameTemplate: null,
                Overwrite: false),
            "weixin-material-upload" => new(
                Type: "weixin-material-upload",
                Template: null,
                ConfigFile: configOverridePath ?? ResolveWeixinMaterialConfigPath(context.WorkflowProjectDir),
                OutputFile: null,
                InputDir: null,
                OutputDir: context.WorkflowProjectDir,
                NameTemplate: null,
                Overwrite: false),
            _ => throw new InvalidOperationException($"未知步骤: {stepType}")
        };

        return new WorkflowDefinition(
            ProjectKey: context.ProjectKey,
            DisplayName: context.DisplayName,
            ProjectDir: context.WorkflowProjectDir,
            ConfigDir: context.ConfigDir,
            Steps: [step]);
    }

    private static async Task<ProjectWorkspaceContext> ApplyRewriteTitleAsync(
        ProjectWorkspaceContext context,
        WorkStateFile state,
        IReadOnlyDictionary<string, string>? outputs,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (outputs is null ||
            !outputs.TryGetValue("title", out var title) ||
            string.IsNullOrWhiteSpace(title))
        {
            return context;
        }

        var sanitizedTitle = SanitizeDirectoryName(title);
        if (string.IsNullOrWhiteSpace(sanitizedTitle) ||
            string.Equals(sanitizedTitle, context.DisplayName, StringComparison.Ordinal))
        {
            return context with { DisplayName = sanitizedTitle };
        }

        var currentDir = context.WorkflowProjectDir;
        var prefix = AreProjectMaterialStepsCompleted(state) ? string.Empty : "_";
        var targetDir = Path.Combine(Path.GetDirectoryName(currentDir)!, $"{prefix}{sanitizedTitle}");

        if (!string.Equals(currentDir, targetDir, StringComparison.Ordinal))
        {
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }

            Directory.Move(currentDir, targetDir);
            RewriteStatePaths(state, currentDir, targetDir);
            state.WorkflowProjectDir = targetDir;

            progress?.Report(new WorkRunEvent(
                context.ProjectKey,
                sanitizedTitle,
                Kind: "project-renamed",
                Message: $"项目目录已更新为 {sanitizedTitle}",
                Ok: true));
        }

        await Task.CompletedTask;
        return context with
        {
            DisplayName = sanitizedTitle,
            WorkflowProjectDir = targetDir,
            VideosOutputDir = Path.Combine(targetDir, "videos"),
            MaterialVideosOutputDir = Path.Combine(targetDir, "material-videos")
        };
    }

    private static async Task<ProjectWorkspaceContext> NormalizeWorkflowDirectoryByProjectMaterialStateAsync(
        ProjectWorkspaceContext context,
        WorkStateFile state,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken)
    {
        var currentDir = context.WorkflowProjectDir;
        var currentName = Path.GetFileName(currentDir);
        var desiredPrefix = AreProjectMaterialStepsCompleted(state) ? string.Empty : "_";
        var bareName = currentName.TrimStart('_');
        var targetDir = Path.Combine(Path.GetDirectoryName(currentDir)!, $"{desiredPrefix}{bareName}");

        if (string.Equals(currentDir, targetDir, StringComparison.Ordinal))
        {
            return context;
        }

        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        Directory.Move(currentDir, targetDir);
        RewriteStatePaths(state, currentDir, targetDir);
        state.WorkflowProjectDir = targetDir;

        progress?.Report(new WorkRunEvent(
            context.ProjectKey,
            context.DisplayName,
            Kind: "project-renamed",
            Message: desiredPrefix.Length == 0
                ? $"项目素材已完成，目录已更新为 {bareName}"
                : $"项目素材未完成，目录已更新为 _{bareName}",
            Ok: true));

        await Task.CompletedTask;
        return context with
        {
            WorkflowProjectDir = targetDir,
            VideosOutputDir = Path.Combine(targetDir, "videos"),
            MaterialVideosOutputDir = Path.Combine(targetDir, "material-videos")
        };
    }

    private static string FinalizeWorkflowDirectory(ProjectWorkspaceContext context)
    {
        var name = Path.GetFileName(context.WorkflowProjectDir);
        if (!name.StartsWith('_'))
        {
            return context.WorkflowProjectDir;
        }

        var finalDir = Path.Combine(Path.GetDirectoryName(context.WorkflowProjectDir)!, name.TrimStart('_'));
        if (Directory.Exists(finalDir))
        {
            Directory.Delete(finalDir, recursive: true);
        }

        Directory.Move(context.WorkflowProjectDir, finalDir);
        return finalDir;
    }

    private static string SanitizeDirectoryName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? value.Trim() : sanitized;
    }

    private static WorkStateFile ReadState(string workflowProjectDir)
    {
        var statePath = Path.Combine(workflowProjectDir, "states.json");
        if (!File.Exists(statePath))
        {
            return new WorkStateFile([], false, workflowProjectDir);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(statePath));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            var steps = root.EnumerateArray().Select(ParseStateStep).ToList();
            return new WorkStateFile(steps, steps.All(step => step.IsCompleted), workflowProjectDir);
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("steps", out var stepsElement) &&
            stepsElement.ValueKind == JsonValueKind.Array)
        {
            var steps = stepsElement.EnumerateArray().Select(ParseStateStep).ToList();
            var completed = root.TryGetProperty("completed", out var completedElement) &&
                completedElement.ValueKind == JsonValueKind.True;
            return new WorkStateFile(steps, completed, workflowProjectDir);
        }

        return new WorkStateFile([], false, workflowProjectDir);
    }

    private static StateStep ParseStateStep(JsonElement element)
    {
        var type = NormalizeStepType(
            element.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty);
        var isCompleted = element.TryGetProperty("isCompleted", out var completedElement) && completedElement.ValueKind == JsonValueKind.True;
        var error = element.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : null;

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("outputs", out var outputsElement) && outputsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in outputsElement.EnumerateObject())
            {
                outputs[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return new StateStep(type, isCompleted, error, outputs);
    }

    private static void MarkCompleted(WorkStateFile state, string stepType, IReadOnlyDictionary<string, string>? outputs)
    {
        stepType = NormalizeStepType(stepType);
        var step = state.Steps.FirstOrDefault(item => string.Equals(item.Type, stepType, StringComparison.Ordinal));
        if (step is null)
        {
            state.Steps.Add(new StateStep(stepType, true, null, ToMutable(outputs)));
            return;
        }

        step.IsCompleted = true;
        step.Error = null;
        step.Outputs = ToMutable(outputs);
    }

    private static void MarkFailed(WorkStateFile state, string stepType, string? error)
    {
        stepType = NormalizeStepType(stepType);
        var step = state.Steps.FirstOrDefault(item => string.Equals(item.Type, stepType, StringComparison.Ordinal));
        if (step is null)
        {
            state.Steps.Add(new StateStep(stepType, false, error, new Dictionary<string, string>(StringComparer.Ordinal)));
            return;
        }

        step.IsCompleted = false;
        step.Error = error;
    }

    private static void InvalidateStep(WorkStateFile state, string stepType)
    {
        stepType = NormalizeStepType(stepType);
        var step = state.Steps.FirstOrDefault(item => string.Equals(item.Type, stepType, StringComparison.Ordinal));
        if (step is null)
        {
            return;
        }

        step.IsCompleted = false;
        step.Error = "短剧信息已更新，需重新执行。";
        step.Outputs = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ToMutable(IReadOnlyDictionary<string, string>? outputs)
    {
        return outputs is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(outputs, StringComparer.Ordinal);
    }

    private static void RewriteStatePaths(WorkStateFile state, string fromDir, string toDir)
    {
        foreach (var step in state.Steps)
        {
            var keys = step.Outputs.Keys.ToList();
            foreach (var key in keys)
            {
                var value = step.Outputs[key];
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (string.Equals(value, fromDir, StringComparison.Ordinal))
                {
                    step.Outputs[key] = toDir;
                    continue;
                }

                if (value.StartsWith(fromDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    step.Outputs[key] = toDir + value[fromDir.Length..];
                }
            }
        }
    }

    private static string NormalizeStepType(string stepType)
    {
        return stepType switch
        {
            "Download" => "download",
            "Transcode" => "transcode",
            "RewriteProjectInfo" => "rewrite",
            "GeneratePosterImage" => "poster-rename",
            "GenerateProjectImages" => "project-image",
            "GenerateCostReportImage" => "cost-report",
            "RenameFiles" => "batch-file-rename",
            "MaterialConvert" => "material-convert",
            "UploadWechatChannel" => "weixin-upload",
            "UploadWechatMaterial" => "weixin-material-upload",
            _ => stepType
        };
    }

    private static string? ResolveWeixinConfigPath(string workflowProjectDir)
    {
        foreach (var fileName in new[]
                 {
                     "weixin-channel-autogen.json",
                     "weixin-channel-submit.json",
                     "weixin-channel-config.json",
                     "weixin-channel-publish-test.json",
                     "weixin-channel-test-no-final-click.json"
                 })
        {
            var candidate = Path.Combine(workflowProjectDir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveWeixinMaterialConfigPath(string workflowProjectDir)
    {
        foreach (var fileName in new[]
                 {
                     "weixin-channel-publish-test.json",
                     "weixin-channel-publish.json",
                     "weixin-channel-material.json"
                 })
        {
            var candidate = Path.Combine(workflowProjectDir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task WriteStateAsync(ProjectWorkspaceContext context, WorkStateFile state, CancellationToken cancellationToken)
    {
        var payload = new
        {
            projectKey = context.ProjectKey,
            displayName = context.DisplayName,
            sourceProjectDir = context.SourceProjectDir,
            workflowProjectDir = context.WorkflowProjectDir,
            updatedAt = DateTimeOffset.Now,
            completed = state.Completed,
            steps = state.Steps.Select(step => new
            {
                type = step.Type,
                isCompleted = step.IsCompleted,
                error = step.Error,
                outputs = step.Outputs
            }).ToArray()
        };

        Directory.CreateDirectory(context.WorkflowProjectDir);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var workflowStatePath = Path.Combine(context.WorkflowProjectDir, "states.json");
        await File.WriteAllTextAsync(workflowStatePath, json, cancellationToken);

        var sourceStatePath = Path.Combine(context.SourceProjectDir, "shortdrama-state.json");
        await File.WriteAllTextAsync(sourceStatePath, json, cancellationToken);
        var sourceLegacyStatePath = Path.Combine(context.SourceProjectDir, "states.json");
        await File.WriteAllTextAsync(sourceLegacyStatePath, json, cancellationToken);

        await SyncSourceProjectMetadataAsync(context, cancellationToken);
    }

    private static async Task SyncSourceProjectMetadataAsync(ProjectWorkspaceContext context, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(context.SourceProjectDir);

        var metadataPath = Path.Combine(context.SourceProjectDir, "shortdrama-project.json");
        JsonObject root;

        if (File.Exists(metadataPath))
        {
            try
            {
                root = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken)) as JsonObject ?? [];
            }
            catch
            {
                root = [];
            }
        }
        else
        {
            root = [];
        }

        root["projectKey"] = context.ProjectKey;
        root["sourceName"] = context.ProjectKey;
        root["displayName"] = context.DisplayName;
        root["title"] = context.DisplayName;
        root["workflowDirName"] = Path.GetFileName(context.WorkflowProjectDir);
        root["workflowProjectDir"] = context.WorkflowProjectDir;
        root["updatedAt"] = DateTimeOffset.Now.ToString("O");

        if (root["createdAt"] is null)
        {
            root["createdAt"] = DateTimeOffset.Now.ToString("O");
        }

        await File.WriteAllTextAsync(
            metadataPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private sealed record ProjectWorkspaceContext(
        string ProjectKey,
        string DisplayName,
        string SourceProjectDir,
        string? BackupProjectDir,
        string WorkflowProjectDir,
        string VideosOutputDir,
        string MaterialVideosOutputDir,
        string? BackupRootDir,
        string ConfigDir,
        string? ConfigFile,
        string CompanyName,
        string? CostReportTemplatePath,
        string? ProjectImageTemplateDir);

    private sealed class WorkStateFile
    {
        public WorkStateFile(List<StateStep> steps, bool completed, string workflowProjectDir)
        {
            Steps = steps;
            Completed = completed;
            WorkflowProjectDir = workflowProjectDir;
        }

        public List<StateStep> Steps { get; }
        public bool Completed { get; set; }
        public string WorkflowProjectDir { get; set; }

        public bool IsCompleted(string stepType) =>
            Steps.Any(step => string.Equals(step.Type, NormalizeStepType(stepType), StringComparison.Ordinal) && step.IsCompleted);

        public StateStep? GetStep(string stepType) =>
            Steps.FirstOrDefault(step => string.Equals(step.Type, NormalizeStepType(stepType), StringComparison.Ordinal));
    }

    private sealed class StateStep
    {
        public StateStep(string type, bool isCompleted, string? error, Dictionary<string, string> outputs)
        {
            Type = type;
            IsCompleted = isCompleted;
            Error = error;
            Outputs = outputs;
        }

        public string Type { get; }
        public bool IsCompleted { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, string> Outputs { get; set; }
    }
}
