using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddShortDramaServices();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "shortdrama-agent"
}));

app.MapPost("/cost-report/build", async Task<IResult> (
    BuildCostReportRequest request,
    ICostReportBuilder costReportBuilder,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ProjectDir) ||
        string.IsNullOrWhiteSpace(request.TemplateDocxPath) ||
        string.IsNullOrWhiteSpace(request.OutputDir))
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "projectDir, templateDocxPath, outputDir are required."
        });
    }

    try
    {
        var result = await costReportBuilder.BuildAsync(
            request.ProjectDir,
            request.TemplateDocxPath,
            request.ConfigDir,
            request.OutputDir,
            cancellationToken);

        return TypedResults.Ok(new
        {
            ok = true,
            outputs = new
            {
                png = result.PngPath,
                docx = result.DocxPath
            },
            project = new
            {
                title = result.Project.Title,
                originalTitle = result.Project.OriginalTitle,
                episodes = result.Project.EpisodeCount,
                minutes = result.Project.TotalMinutes,
                costWan = result.Project.CostAmountWan,
                company = result.Project.CompanyName
            }
        });
    }
    catch (Exception ex)
    {
        return TypedResults.Problem(
            detail: ex.Message,
            title: "Cost report build failed",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/transcode/batch", async Task<IResult> (
    TranscodeBatchRequest request,
    IVideoTranscoder videoTranscoder,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ProjectDir))
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "projectDir is required."
        });
    }

    var inputDir = string.IsNullOrWhiteSpace(request.InputDir)
        ? Path.Combine(request.ProjectDir, "videos")
        : request.InputDir;

    var outputDir = string.IsNullOrWhiteSpace(request.OutputDir)
        ? Path.Combine(request.ProjectDir, "transcoded")
        : request.OutputDir;

    try
    {
        var result = await videoTranscoder.TranscodeAsync(
            new VideoTranscodeRequest(
                request.ProjectDir,
                inputDir,
                outputDir,
                request.ConfigFile,
                request.Overwrite,
                request.Crf ?? 23,
                request.Preset ?? "fast"),
            progress: null,
            cancellationToken);

        return TypedResults.Ok(new
        {
            ok = result.FailedFiles == 0,
            inputDir,
            outputDir,
            totalFiles = result.TotalFiles,
            transcodedFiles = result.TranscodedFiles,
            skippedFiles = result.SkippedFiles,
            failedFiles = result.FailedFiles,
            outputs = result.Outputs,
            failures = result.Failures
        });
    }
    catch (Exception ex)
    {
        return TypedResults.Problem(
            detail: ex.Message,
            title: "Video transcode failed",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/project-image/generate", async Task<IResult> (
    GenerateProjectImageRequest request,
    IProjectImageGenerator projectImageGenerator,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ProjectDir))
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "projectDir is required."
        });
    }

    try
    {
        var result = await projectImageGenerator.GenerateAsync(
            new ProjectImageGenerateRequest(
                request.ProjectDir,
                string.IsNullOrWhiteSpace(request.InputDir)
                    ? Path.Combine(request.ProjectDir, "videos")
                    : request.InputDir,
                string.IsNullOrWhiteSpace(request.OutputDir)
                    ? request.ProjectDir
                    : request.OutputDir,
                request.TemplateImageDir,
                request.ConfigFile,
                request.Count,
                request.Overwrite),
            cancellationToken);

        return TypedResults.Ok(new
        {
            ok = true,
            count = result.Count,
            outputs = result.Outputs
        });
    }
    catch (Exception ex)
    {
        return TypedResults.Problem(
            detail: ex.Message,
            title: "Project image generation failed",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/rewrite/project-info", async Task<IResult> (
    RewriteProjectInfoRequest request,
    IProjectInfoRewriter projectInfoRewriter,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ProjectDir) || string.IsNullOrWhiteSpace(request.ConfigFile))
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "projectDir and configFile are required."
        });
    }

    try
    {
        var result = await projectInfoRewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(
                request.ProjectDir,
                request.ConfigFile,
                request.OutputFile ?? Path.Combine(request.ProjectDir, "短剧信息_改写.txt"),
                request.Overwrite),
            cancellationToken);

        return TypedResults.Ok(new
        {
            ok = true,
            outputFile = result.OutputFilePath,
            title = result.Title,
            tagline = result.Tagline,
            synopsis = result.Synopsis,
            shortTitle = result.ShortTitle,
            tags = result.Tags
        });
    }
    catch (Exception ex)
    {
        return TypedResults.Problem(
            detail: ex.Message,
            title: "Project info rewrite failed",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/poster-rename/run", async Task<IResult> (
    PosterRenameApiRequest request,
    IPosterRenamer posterRenamer,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ProjectDir))
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "projectDir is required."
        });
    }

    try
    {
        var result = await posterRenamer.RenameAsync(
            new PosterRenameRequest(
                request.ProjectDir,
                request.InputFile,
                request.OutputFile,
                request.ConfigFile,
                request.NameTemplate,
                request.UseAi,
                request.Overwrite),
            cancellationToken);

        return TypedResults.Ok(new
        {
            ok = true,
            inputFile = result.InputFilePath,
            outputFile = result.OutputFilePath,
            posterName = result.PosterName
        });
    }
    catch (Exception ex)
    {
        return TypedResults.Problem(
            detail: ex.Message,
            title: "Poster rename failed",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/batch-file-rename/run", async Task<IResult> (
    BatchFileRenameApiRequest request,
    IBatchFileRenamer batchFileRenamer,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ProjectDir))
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "projectDir is required."
        });
    }

    try
    {
        var result = await batchFileRenamer.RenameAsync(
            new BatchFileRenameRequest(
                request.ProjectDir,
                string.IsNullOrWhiteSpace(request.InputDir)
                    ? Path.Combine(request.ProjectDir, "videos")
                    : request.InputDir,
                request.ConfigFile,
                request.NameTemplate,
                request.Overwrite),
            cancellationToken);

        return TypedResults.Ok(new
        {
            ok = true,
            renamedCount = result.RenamedCount,
            items = result.Items.Select(item => new
            {
                inputFile = item.InputFilePath,
                outputFile = item.OutputFilePath
            })
        });
    }
    catch (Exception ex)
    {
        return TypedResults.Problem(
            detail: ex.Message,
            title: "Batch file rename failed",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/workflow/run", async Task<IResult> (
    WorkflowRunRequest request,
    IWorkflowRunner workflowRunner,
    CancellationToken cancellationToken) =>
{
    if (request.Definition is null)
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "definition is required."
        });
    }

    if (string.IsNullOrWhiteSpace(request.Definition.ProjectDir))
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "definition.projectDir is required."
        });
    }

    if (request.Definition.Steps.Count == 0)
    {
        return TypedResults.BadRequest(new
        {
            ok = false,
            errorCode = "INVALID_REQUEST",
            message = "definition.steps must not be empty."
        });
    }

    try
    {
        var result = await workflowRunner.RunAsync(request.Definition, progress: null, cancellationToken);

        return TypedResults.Ok(new
        {
            ok = result.Ok,
            steps = result.Steps.Select(step => new
            {
                type = step.Type,
                ok = step.Ok,
                errorCode = step.ErrorCode,
                message = step.Message,
                outputs = step.Outputs
            })
        });
    }
    catch (Exception ex)
    {
        return TypedResults.Problem(
            detail: ex.Message,
            title: "Workflow run failed",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

public sealed record BuildCostReportRequest(
    string ProjectDir,
    string TemplateDocxPath,
    string? ConfigDir,
    string OutputDir);

public sealed record TranscodeBatchRequest(
    string ProjectDir,
    string? InputDir,
    string? OutputDir,
    string? ConfigFile,
    bool Overwrite = false,
    int? Crf = null,
    string? Preset = null);

public sealed record GenerateProjectImageRequest(
    string ProjectDir,
    string? InputDir,
    string? OutputDir,
    string? TemplateImageDir,
    string? ConfigFile,
    int? Count = null,
    bool Overwrite = false);

public sealed record RewriteProjectInfoRequest(
    string ProjectDir,
    string ConfigFile,
    string? OutputFile,
    bool Overwrite = false);

public sealed record PosterRenameApiRequest(
    string ProjectDir,
    string? InputFile,
    string? OutputFile,
    string? ConfigFile,
    string? NameTemplate,
    bool UseAi = false,
    bool Overwrite = false);

public sealed record BatchFileRenameApiRequest(
    string ProjectDir,
    string? InputDir,
    string? ConfigFile,
    string? NameTemplate,
    bool Overwrite = false);

public sealed record WorkflowRunRequest(WorkflowDefinition? Definition);
