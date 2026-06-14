using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class TranscodeBatchCommand
{
    private readonly IVideoTranscoder _videoTranscoder;
    private readonly ILogger<TranscodeBatchCommand> _logger;

    public TranscodeBatchCommand(
        IVideoTranscoder videoTranscoder,
        ILogger<TranscodeBatchCommand> logger)
    {
        _videoTranscoder = videoTranscoder;
        _logger = logger;
    }

    public Command Create()
    {
        var transcode = new Command("transcode", "Batch transcode shortdrama video files");
        var batch = new Command("batch", "Transcode videos to mp4 (H.264/AAC)");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Project directory. Defaults input to <project>/videos and output to <project>/transcoded")
        {
            IsRequired = true
        };

        var inputDirOption = new Option<DirectoryInfo?>(
            "--input-dir",
            "Optional input directory. Defaults to <project-dir>/videos");

        var outputDirOption = new Option<DirectoryInfo?>(
            "--output-dir",
            "Optional output directory. Defaults to <project-dir>/transcoded");

        var configFileOption = new Option<FileInfo?>(
            "--config-file",
            "Optional config.txt path. When set, video bitrate/fps/resolution settings are loaded from it.");

        var overwriteOption = new Option<bool>(
            "--overwrite",
            "Overwrite existing transcoded outputs");

        var crfOption = new Option<int>(
            "--crf",
            () => 23,
            "H.264 CRF quality. Lower means higher quality.");

        var presetOption = new Option<string>(
            "--preset",
            () => "fast",
            "FFmpeg x264 preset, for example ultrafast, fast, medium, slow.");

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        batch.AddOption(projectDirOption);
        batch.AddOption(inputDirOption);
        batch.AddOption(outputDirOption);
        batch.AddOption(configFileOption);
        batch.AddOption(overwriteOption);
        batch.AddOption(crfOption);
        batch.AddOption(presetOption);
        batch.AddOption(jsonOutputOption);

        batch.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            var inputDir = context.ParseResult.GetValueForOption(inputDirOption);
            var outputDir = context.ParseResult.GetValueForOption(outputDirOption);
            var configFile = context.ParseResult.GetValueForOption(configFileOption);
            var overwrite = context.ParseResult.GetValueForOption(overwriteOption);
            var crf = context.ParseResult.GetValueForOption(crfOption);
            var preset = context.ParseResult.GetValueForOption(presetOption)!;
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                projectDir.FullName,
                inputDir?.FullName,
                outputDir?.FullName,
                configFile?.FullName,
                overwrite,
                crf,
                preset,
                jsonOutput,
                context.GetCancellationToken());
        });

        transcode.AddCommand(batch);
        return transcode;
    }

    public async Task<int> ExecuteAsync(
        string projectDir,
        string? inputDir,
        string? outputDir,
        string? configFile,
        bool overwrite,
        int crf,
        string preset,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedInputDir = inputDir ?? Path.Combine(projectDir, "videos");
            var resolvedOutputDir = outputDir ?? Path.Combine(projectDir, "transcoded");

            var result = await _videoTranscoder.TranscodeAsync(
                new VideoTranscodeRequest(
                    projectDir,
                    resolvedInputDir,
                    resolvedOutputDir,
                    configFile,
                    overwrite,
                    crf,
                    preset),
                progress: null,
                cancellationToken);

            if (jsonOutput)
            {
                WriteSuccessJson(resolvedInputDir, resolvedOutputDir, result);
            }
            else
            {
                if (result.FailedFiles == 0)
                {
                    _logger.LogInformation(
                        "Transcode complete. total={Total}, transcoded={Transcoded}, skipped={Skipped}, failed={Failed}, output={OutputDir}",
                        result.TotalFiles,
                        result.TranscodedFiles,
                        result.SkippedFiles,
                        result.FailedFiles,
                        resolvedOutputDir);
                }
                else
                {
                    _logger.LogError(
                        "Transcode finished with failures. total={Total}, transcoded={Transcoded}, skipped={Skipped}, failed={Failed}, firstFailure={FirstFailure}, output={OutputDir}",
                        result.TotalFiles,
                        result.TranscodedFiles,
                        result.SkippedFiles,
                        result.FailedFiles,
                        Path.GetFileName(result.Failures[0].InputPath),
                        resolvedOutputDir);
                }
            }

            return result.FailedFiles == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transcode videos");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "TRANSCODE_FAILED",
                  "message": "{{EscapeJson(ex.Message)}}"
                }
                """);
            }

            return 1;
        }
    }

    private static void WriteSuccessJson(string inputDir, string outputDir, VideoTranscodeResult result)
    {
        var outputs = string.Join(",\n", result.Outputs.Select(path => $"    \"{EscapeJson(path)}\""));

        Console.WriteLine($$"""
        {
          "ok": true,
          "inputDir": "{{EscapeJson(inputDir)}}",
          "outputDir": "{{EscapeJson(outputDir)}}",
          "totalFiles": {{result.TotalFiles}},
          "transcodedFiles": {{result.TranscodedFiles}},
          "skippedFiles": {{result.SkippedFiles}},
          "failedFiles": {{result.FailedFiles}},
          "outputs": [
        {{outputs}}
          ],
          "failures": [
        {{string.Join(",\n", result.Failures.Select(failure => $$"""
            {
              "inputPath": "{{EscapeJson(failure.InputPath)}}",
              "outputPath": "{{EscapeJson(failure.OutputPath)}}",
              "message": "{{EscapeJson(failure.Message)}}"
            }
        """))}}
          ]
        }
        """);
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
