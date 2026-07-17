using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class WeixinUploadCommand
{
    private readonly IWeixinChannelUploader _weixinChannelUploader;
    private readonly IWorkflowInteractionService _interactionService;
    private readonly ILogger<WeixinUploadCommand> _logger;

    public WeixinUploadCommand(
        IWeixinChannelUploader weixinChannelUploader,
        IWorkflowInteractionService interactionService,
        ILogger<WeixinUploadCommand> logger)
    {
        _weixinChannelUploader = weixinChannelUploader;
        _interactionService = interactionService;
        _logger = logger;
    }

    public Command Create()
    {
        var weixin = new Command("weixin", "Run Weixin automation flows");
        var upload = new Command("upload", "Run Weixin upload against a project");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Workflow or project directory")
        {
            IsRequired = true
        };
        var configPathOption = new Option<FileInfo?>(
            "--config",
            "Optional Weixin config json path");
        var configNameOption = new Option<string?>(
            "--config-name",
            "Optional Weixin config file name");
        var autoResumeOption = new Option<bool>(
            "--auto-resume",
            () => true,
            "Automatically resume when the flow requests manual confirmation");
        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        upload.AddOption(projectDirOption);
        upload.AddOption(configPathOption);
        upload.AddOption(configNameOption);
        upload.AddOption(autoResumeOption);
        upload.AddOption(jsonOutputOption);

        upload.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            var configPath = context.ParseResult.GetValueForOption(configPathOption);
            var configName = context.ParseResult.GetValueForOption(configNameOption);
            var autoResume = context.ParseResult.GetValueForOption(autoResumeOption);
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                projectDir.FullName,
                configPath?.FullName,
                configName,
                autoResume,
                jsonOutput,
                context.GetCancellationToken());
        });

        weixin.AddCommand(upload);
        return weixin;
    }

    public async Task<int> ExecuteAsync(
        string projectDir,
        string? configPath,
        string? configName,
        bool autoResume,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        Action<WorkflowInteractionRequest?>? handler = null;
        if (autoResume)
        {
            handler = request =>
            {
                if (request is null)
                {
                    return;
                }

                _logger.LogInformation("Interaction requested. stage={Stage}, message={Message}", request.Stage, request.Message);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500, cancellationToken);
                    _interactionService.TryResolve("resume");
                }, cancellationToken);
            };
            _interactionService.RequestChanged += handler;
        }

        try
        {
            var normalizedProjectDir = Path.GetFullPath(projectDir);
            var displayName = Path.GetFileName(normalizedProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var result = await _weixinChannelUploader.UploadAsync(
                new WeixinUploadRequest(
                    ProjectKey: displayName,
                    ProjectDir: normalizedProjectDir,
                    DisplayName: displayName,
                    ConfigPath: configPath,
                    ConfigName: configName),
                new Progress<string>(message => _logger.LogInformation("{Message}", message)),
                cancellationToken);

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": {{result.Ok.ToString().ToLowerInvariant()}},
                  "projectDir": "{{EscapeJson(result.ProjectDir)}}",
                  "configPath": {{ToNullableJson(result.ConfigPath)}},
                  "message": {{ToNullableJson(result.Message)}}
                }
                """);
            }
            else
            {
                Console.WriteLine(result.Message ?? (result.Ok ? "执行完成" : "执行失败"));
            }

            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Weixin upload");
            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "message": "{{EscapeJson(ex.Message)}}"
                }
                """);
            }

            return 1;
        }
        finally
        {
            if (handler is not null)
            {
                _interactionService.RequestChanged -= handler;
            }
        }
    }

    private static string ToNullableJson(string? value)
    {
        return value is null ? "null" : $"\"{EscapeJson(value)}\"";
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
