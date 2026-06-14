using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;

namespace ShortDrama.Infrastructure.Workflow;

public sealed class WorkflowRunner : IWorkflowRunner
{
    private readonly ICostReportBuilder _costReportBuilder;
    private readonly IBatchFileRenamer _batchFileRenamer;
    private readonly IPosterRenamer _posterRenamer;
    private readonly IProjectInfoRewriter _projectInfoRewriter;
    private readonly IProjectImageGenerator _projectImageGenerator;
    private readonly IVideoTranscoder _videoTranscoder;
    private readonly IVideoMaterialConverter _videoMaterialConverter;
    private readonly IDramaDownloader _dramaDownloader;
    private readonly IWeixinChannelUploader _weixinChannelUploader;

    public WorkflowRunner(
        ICostReportBuilder costReportBuilder,
        IBatchFileRenamer batchFileRenamer,
        IPosterRenamer posterRenamer,
        IProjectInfoRewriter projectInfoRewriter,
        IProjectImageGenerator projectImageGenerator,
        IVideoTranscoder videoTranscoder,
        IVideoMaterialConverter videoMaterialConverter,
        IDramaDownloader dramaDownloader,
        IWeixinChannelUploader weixinChannelUploader)
    {
        _costReportBuilder = costReportBuilder;
        _batchFileRenamer = batchFileRenamer;
        _posterRenamer = posterRenamer;
        _projectInfoRewriter = projectInfoRewriter;
        _projectImageGenerator = projectImageGenerator;
        _videoTranscoder = videoTranscoder;
        _videoMaterialConverter = videoMaterialConverter;
        _dramaDownloader = dramaDownloader;
        _weixinChannelUploader = weixinChannelUploader;
    }

    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition definition,
        IProgress<WorkflowRuntimeEvent>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stepResults = new List<WorkflowStepResult>();

        foreach (var step in definition.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await RunStepAsync(definition, step, progress, cancellationToken);
            stepResults.Add(result);

            if (!result.Ok && !step.ContinueOnError)
            {
                break;
            }
        }

        var ok = stepResults.All(step => step.Ok);
        return new WorkflowRunResult(ok, stepResults);
    }

    private async Task<WorkflowStepResult> RunStepAsync(
        WorkflowDefinition definition,
        WorkflowStep step,
        IProgress<WorkflowRuntimeEvent>? progress,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(0, step.Retry) + 1;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await RunStepOnceAsync(definition, step, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                if (attempt >= attempts)
                {
                    return new WorkflowStepResult(
                        Type: step.Type,
                        Ok: false,
                        ErrorCode: "STEP_FAILED",
                        Message: ex.Message);
                }
            }
        }

        return new WorkflowStepResult(
            Type: step.Type,
            Ok: false,
            ErrorCode: "STEP_FAILED",
            Message: "Unknown workflow failure.");
    }

    private async Task<WorkflowStepResult> RunStepOnceAsync(
        WorkflowDefinition definition,
        WorkflowStep step,
        IProgress<WorkflowRuntimeEvent>? progress,
        CancellationToken cancellationToken)
    {
        switch (step.Type)
        {
            case "download":
            {
                var outputDir = string.IsNullOrWhiteSpace(step.OutputDir)
                    ? definition.ProjectDir
                    : step.OutputDir;

                var metadata = ProjectAutomationMetadata.Resolve(outputDir);
                var result = await _dramaDownloader.DownloadAsync(
                    new DramaDownloadRequest(
                        ProjectDir: definition.ProjectDir,
                        OutputDir: outputDir,
                        DisplayName: string.IsNullOrWhiteSpace(metadata.Title) ? Path.GetFileName(outputDir) : metadata.Title,
                        BookId: metadata.BookId,
                        Episodes: metadata.Episodes,
                        Quality: metadata.Quality,
                        Concurrent: metadata.Concurrent),
                    new InlineProgress<string>(message =>
                    {
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            progress?.Report(new WorkflowRuntimeEvent(step.Type, "step-output", message));
                        }
                    }),
                    cancellationToken);

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: result.Ok,
                    ErrorCode: result.Ok ? null : "STEP_FAILED",
                    Message: result.Message,
                    Outputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["outputDir"] = result.OutputDir,
                        ["videoCount"] = result.VideoCount.ToString()
                    });
            }

            case "batch-file-rename":
            {
                var inputDir = string.IsNullOrWhiteSpace(step.InputDir)
                    ? Path.Combine(definition.ProjectDir, "videos")
                    : step.InputDir;

                var result = await _batchFileRenamer.RenameAsync(
                    new BatchFileRenameRequest(
                        definition.ProjectDir,
                        inputDir,
                        step.ConfigFile,
                        step.NameTemplate,
                        step.Overwrite ?? false),
                    cancellationToken);

                var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["renamedCount"] = result.RenamedCount.ToString()
                };

                for (var index = 0; index < result.Items.Count; index++)
                {
                    outputs[$"file{index + 1}"] = result.Items[index].OutputFilePath;
                }

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: true,
                    Outputs: outputs);
            }

            case "poster-rename":
            {
                var inputFile = step.InputDir;
                if (string.IsNullOrWhiteSpace(inputFile))
                {
                    inputFile = Directory.EnumerateFiles(definition.ProjectDir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(path =>
                        {
                            var ext = Path.GetExtension(path);
                            return string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".heic", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".heif", StringComparison.OrdinalIgnoreCase);
                        })
                        .Where(path =>
                        {
                            var fileName = Path.GetFileNameWithoutExtension(path);
                            return !fileName.StartsWith("工程图_", StringComparison.Ordinal) &&
                                   !fileName.StartsWith("成本报表", StringComparison.Ordinal) &&
                                   !string.Equals(fileName, "seal", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(fileName, "sign", StringComparison.OrdinalIgnoreCase) &&
                                   !fileName.StartsWith("seal.prepared", StringComparison.Ordinal);
                        })
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                }

                var result = await _posterRenamer.RenameAsync(
                    new PosterRenameRequest(
                        definition.ProjectDir,
                        inputFile,
                        step.OutputFile,
                        step.ConfigFile,
                        step.NameTemplate,
                        false,
                        step.Overwrite ?? false),
                    cancellationToken);

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: true,
                    Outputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["inputFile"] = result.InputFilePath,
                        ["outputFile"] = result.OutputFilePath,
                        ["posterName"] = result.PosterName
                    });
            }

            case "rewrite":
            {
                if (string.IsNullOrWhiteSpace(step.ConfigFile))
                {
                    throw new InvalidOperationException("rewrite step 缺少 configFile。");
                }

                var result = await _projectInfoRewriter.RewriteAsync(
                    new ProjectInfoRewriteRequest(
                        definition.ProjectDir,
                        step.ConfigFile,
                        step.OutputFile ?? Path.Combine(definition.ProjectDir, "短剧信息_改写.txt"),
                        step.Overwrite ?? false),
                    cancellationToken);

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: true,
                    Outputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["outputFile"] = result.OutputFilePath,
                        ["title"] = result.Title,
                        ["tagline"] = result.Tagline,
                        ["synopsis"] = result.Synopsis,
                        ["shortTitle"] = result.ShortTitle,
                        ["tags"] = result.Tags
                    });
            }

            case "project-image":
            {
                var inputDir = string.IsNullOrWhiteSpace(step.InputDir)
                    ? Path.Combine(definition.ProjectDir, "videos")
                    : step.InputDir;

                var outputDir = string.IsNullOrWhiteSpace(step.OutputDir)
                    ? definition.ProjectDir
                    : step.OutputDir;

                var result = await _projectImageGenerator.GenerateAsync(
                    new ProjectImageGenerateRequest(
                        definition.ProjectDir,
                        inputDir,
                        outputDir,
                        step.Template,
                        step.ConfigFile,
                        step.Count,
                        step.Overwrite ?? false),
                    cancellationToken);

                var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = result.Count.ToString()
                };

                for (var index = 0; index < result.Outputs.Count; index++)
                {
                    outputs[$"image{index + 1}"] = result.Outputs[index];
                }

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: true,
                    Outputs: outputs);
            }

            case "transcode":
            {
                var inputDir = string.IsNullOrWhiteSpace(step.InputDir)
                    ? Path.Combine(definition.ProjectDir, "videos")
                    : step.InputDir;

                var outputDir = string.IsNullOrWhiteSpace(step.OutputDir)
                    ? Path.Combine(definition.ProjectDir, "transcoded")
                    : step.OutputDir;

                var result = await _videoTranscoder.TranscodeAsync(
                    new VideoTranscodeRequest(
                        definition.ProjectDir,
                        inputDir,
                        outputDir,
                        step.ConfigFile,
                        step.Overwrite ?? false,
                        step.Crf ?? 23,
                        step.Preset ?? "fast"),
                    new InlineProgress<VideoTranscodeProgress>(evt =>
                    {
                        var message = evt.Kind switch
                        {
                            "file-started" => $"正在转码 {evt.Index}/{evt.Total}: {Path.GetFileName(evt.InputPath)}",
                            "file-skipped" => $"跳过转码 {evt.Index}/{evt.Total}: {Path.GetFileName(evt.InputPath)}",
                            "file-completed" => $"转码完成 {evt.Index}/{evt.Total}: {Path.GetFileName(evt.OutputPath)}，耗时 {evt.Elapsed}",
                            "file-failed" => $"转码失败 {evt.Index}/{evt.Total}: {Path.GetFileName(evt.InputPath)}，原因: {evt.Message}",
                            _ => null
                        };

                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            progress?.Report(new WorkflowRuntimeEvent(step.Type, evt.Kind, message));
                        }
                    }),
                    cancellationToken);

                var allPendingDownloads = result.FailedFiles > 0 &&
                    result.Failures.All(failure => IsPendingTranscodeFailure(failure.Message));

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: result.FailedFiles == 0,
                    ErrorCode: result.FailedFiles == 0
                        ? null
                        : allPendingDownloads
                            ? "STEP_PENDING"
                            : "STEP_FAILED",
                    Message: result.FailedFiles == 0
                        ? null
                        : allPendingDownloads
                            ? $"有 {result.FailedFiles} 个视频仍在下载或未下载完成，可继续运行。首个文件: {Path.GetFileName(result.Failures[0].InputPath)}"
                            : $"有 {result.FailedFiles} 个视频转码失败，首个失败文件: {Path.GetFileName(result.Failures[0].InputPath)}，原因: {result.Failures[0].Message}",
                    Outputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["inputDir"] = inputDir,
                        ["outputDir"] = outputDir,
                        ["totalFiles"] = result.TotalFiles.ToString(),
                        ["transcodedFiles"] = result.TranscodedFiles.ToString(),
                        ["skippedFiles"] = result.SkippedFiles.ToString(),
                        ["failedFiles"] = result.FailedFiles.ToString()
                    });
            }

            case "material-convert":
            {
                var inputDir = string.IsNullOrWhiteSpace(step.InputDir)
                    ? Path.Combine(definition.ProjectDir, "videos")
                    : step.InputDir;

                var outputDir = string.IsNullOrWhiteSpace(step.OutputDir)
                    ? Path.Combine(definition.ProjectDir, "material-videos")
                    : step.OutputDir;

                var result = await _videoMaterialConverter.ConvertAsync(
                    new VideoMaterialConvertRequest(
                        definition.ProjectDir,
                        inputDir,
                        outputDir,
                        step.ConfigFile,
                        step.Overwrite ?? false),
                    new InlineProgress<VideoMaterialConvertProgress>(evt =>
                    {
                        var message = evt.Kind switch
                        {
                            "file-started" => $"正在转换素材 {evt.Index}/{evt.Total}: {Path.GetFileName(evt.InputPath)}",
                            "file-skipped" => $"跳过素材转换 {evt.Index}/{evt.Total}: {Path.GetFileName(evt.InputPath)}",
                            "file-completed" => $"素材转换完成 {evt.Index}/{evt.Total}: {Path.GetFileName(evt.OutputPath)}，耗时 {evt.Elapsed}",
                            "file-failed" => $"素材转换失败 {evt.Index}/{evt.Total}: {Path.GetFileName(evt.InputPath)}，原因: {evt.Message}",
                            _ => null
                        };

                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            progress?.Report(new WorkflowRuntimeEvent(step.Type, evt.Kind, message));
                        }
                    }),
                    cancellationToken);

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: result.FailedFiles == 0,
                    ErrorCode: result.FailedFiles == 0 ? null : "STEP_FAILED",
                    Message: result.FailedFiles == 0
                        ? null
                        : $"有 {result.FailedFiles} 个素材视频转换失败，首个失败文件: {Path.GetFileName(result.Failures[0].InputPath)}，原因: {result.Failures[0].Message}",
                    Outputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["inputDir"] = inputDir,
                        ["outputDir"] = outputDir,
                        ["totalFiles"] = result.TotalFiles.ToString(),
                        ["convertedFiles"] = result.ConvertedFiles.ToString(),
                        ["skippedFiles"] = result.SkippedFiles.ToString(),
                        ["failedFiles"] = result.FailedFiles.ToString()
                    });
            }

            case "cost-report":
            {
                var outputDir = string.IsNullOrWhiteSpace(step.OutputDir)
                    ? definition.ProjectDir
                    : step.OutputDir;

                var result = await _costReportBuilder.BuildAsync(
                    definition.ProjectDir,
                    step.Template ?? string.Empty,
                    definition.ConfigDir,
                    outputDir,
                    cancellationToken);

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: true,
                    Outputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["png"] = result.PngPath,
                        ["docx"] = result.DocxPath
                    });
            }

            case "weixin-upload":
            case "weixin-material-upload":
            {
                var projectDir = string.IsNullOrWhiteSpace(step.OutputDir)
                    ? definition.ProjectDir
                    : step.OutputDir;
                var metadata = ProjectAutomationMetadata.Resolve(projectDir);

                var result = await _weixinChannelUploader.UploadAsync(
                    new WeixinUploadRequest(
                        ProjectKey: definition.ProjectKey,
                        ProjectDir: projectDir,
                        DisplayName: definition.DisplayName,
                        ConfigPath: step.ConfigFile,
                        ConfigName: metadata.UploadConfigName),
                    new InlineProgress<string>(message =>
                    {
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            progress?.Report(new WorkflowRuntimeEvent(step.Type, "step-output", message));
                        }
                    }),
                    cancellationToken);

                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: result.Ok,
                    ErrorCode: result.Ok ? null : "STEP_FAILED",
                    Message: result.Message,
                    Outputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["projectDir"] = result.ProjectDir,
                        ["configPath"] = result.ConfigPath ?? string.Empty
                    });
            }

            default:
                return new WorkflowStepResult(
                    Type: step.Type,
                    Ok: false,
                    ErrorCode: "NOT_IMPLEMENTED",
                    Message: $"Step type is not implemented: {step.Type}");
        }
    }

    private static bool IsPendingTranscodeFailure(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
               message.Contains("仍在下载或未下载完成", StringComparison.Ordinal);
    }
}
