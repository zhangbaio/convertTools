using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;

namespace ShortDrama.Cli.Commands;

public sealed class WorkScanCommand
{
    private readonly IProjectScanner _projectScanner;
    private readonly IWorkService _workService;
    private readonly ILogger<WorkScanCommand> _logger;

    public WorkScanCommand(
        IProjectScanner projectScanner,
        IWorkService workService,
        ILogger<WorkScanCommand> logger)
    {
        _projectScanner = projectScanner;
        _workService = workService;
        _logger = logger;
    }

    public Command Create()
    {
        var work = new Command("work", "Scan project roots and show pending shortdrama work");
        var scan = new Command("scan", "Scan source/workflow/backup directories and resolve resumable project states");
        var run = new Command("run", "Process pending projects into workflow directories");

        var rootDirOption = new Option<DirectoryInfo>(
            "--root-dir",
            "Root directory that contains source project folders, workflow, and config")
        {
            IsRequired = true
        };

        var backupRootDirOption = new Option<DirectoryInfo?>(
            "--backup-root-dir",
            "Optional backup root directory. Defaults to sibling backup directory.");

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        scan.AddOption(rootDirOption);
        scan.AddOption(backupRootDirOption);
        scan.AddOption(jsonOutputOption);

        var forceOption = new Option<bool>(
            "--force",
            "Re-run projects even if they are already marked completed");

        scan.SetHandler(async (InvocationContext context) =>
        {
            var rootDir = context.ParseResult.GetValueForOption(rootDirOption)!;
            var backupRootDir = context.ParseResult.GetValueForOption(backupRootDirOption);
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                rootDir.FullName,
                backupRootDir?.FullName,
                jsonOutput,
                context.GetCancellationToken());
        });

        run.AddOption(rootDirOption);
        run.AddOption(backupRootDirOption);
        run.AddOption(forceOption);
        run.AddOption(jsonOutputOption);

        run.SetHandler(async (InvocationContext context) =>
        {
            var rootDir = context.ParseResult.GetValueForOption(rootDirOption)!;
            var backupRootDir = context.ParseResult.GetValueForOption(backupRootDirOption);
            var force = context.ParseResult.GetValueForOption(forceOption);
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteRunAsync(
                rootDir.FullName,
                backupRootDir?.FullName,
                force,
                jsonOutput,
                context.GetCancellationToken());
        });

        work.AddCommand(scan);
        work.AddCommand(run);
        return work;
    }

    public async Task<int> ExecuteAsync(
        string rootDir,
        string? backupRootDir,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _projectScanner.ScanAsync(rootDir, backupRootDir, cancellationToken);

            if (jsonOutput)
            {
                WriteJson(result);
            }
            else
            {
                WriteTable(result);
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan work root");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "WORK_SCAN_FAILED",
                  "message": "{{EscapeJson(ex.Message)}}"
                }
                """);
            }

            return 1;
        }
    }

    public async Task<int> ExecuteRunAsync(
        string rootDir,
        string? backupRootDir,
        bool force,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var progress = jsonOutput ? null : new Progress<WorkRunEvent>(WriteRunEvent);
            var result = await _workService.RunAsync(rootDir, backupRootDir, force, progress, cancellationToken);
            stopwatch.Stop();

            if (jsonOutput)
            {
                var projects = string.Join(",\n", result.Projects.Select(project => $$"""
                    {
                      "projectKey": "{{EscapeJson(project.ProjectKey)}}",
                      "displayName": "{{EscapeJson(project.DisplayName)}}",
                      "workflowProjectDir": "{{EscapeJson(project.WorkflowProjectDir)}}",
                      "ok": {{project.Ok.ToString().ToLowerInvariant()}},
                      "skipped": {{project.Skipped.ToString().ToLowerInvariant()}},
                      "message": {{ToNullableJson(project.Message)}}
                    }
                """));

                Console.WriteLine($$"""
                {
                  "ok": {{(result.FailedProjects == 0).ToString().ToLowerInvariant()}},
                  "rootDir": "{{EscapeJson(result.RootDir)}}",
                  "backupRootDir": {{ToNullableJson(result.BackupRootDir)}},
                  "totalProjects": {{result.TotalProjects}},
                  "succeededProjects": {{result.SucceededProjects}},
                  "failedProjects": {{result.FailedProjects}},
                  "skippedProjects": {{result.SkippedProjects}},
                  "projects": [
                {{projects}}
                  ]
                }
                """);
            }
            else
            {
                WriteRunSummary(result, stopwatch.Elapsed);
            }

            return result.FailedProjects == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run work root");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "WORK_RUN_FAILED",
                  "message": "{{EscapeJson(ex.Message)}}"
                }
                """);
            }

            return 1;
        }
    }

    private static void WriteJson(ProjectScanResult result)
    {
        var projects = string.Join(",\n", result.Projects.Select(project => $$"""
            {
              "projectKey": "{{EscapeJson(project.ProjectKey)}}",
              "sourceName": "{{EscapeJson(project.SourceName)}}",
              "displayName": "{{EscapeJson(project.DisplayName)}}",
              "sourceProjectDir": "{{EscapeJson(project.SourceProjectDir)}}",
              "workflowProjectDir": {{ToNullableJson(project.WorkflowProjectDir)}},
              "backupProjectDir": {{ToNullableJson(project.BackupProjectDir)}},
              "status": "{{EscapeJson(project.Status)}}",
              "videoCount": {{project.VideoCount}},
              "completedSteps": {{project.CompletedSteps}},
              "totalSteps": {{project.TotalSteps}},
              "resumeFrom": {{ToNullableJson(project.ResumeFrom)}},
              "failedStep": {{ToNullableJson(project.FailedStep)}}
            }
        """));

        Console.WriteLine($$"""
        {
          "ok": true,
          "rootDir": "{{EscapeJson(result.RootDir)}}",
          "backupRootDir": {{ToNullableJson(result.BackupRootDir)}},
          "totalProjects": {{result.TotalProjects}},
          "pendingProjects": {{result.PendingProjects}},
          "projects": [
        {{projects}}
          ]
        }
        """);
    }

    private static void WriteTable(ProjectScanResult result)
    {
        Console.WriteLine($"共找到 {result.TotalProjects} 个项目，其中 {result.PendingProjects} 个待处理");
        Console.WriteLine();
        Console.WriteLine("待处理项目");
        Console.WriteLine(new string('-', 88));
        Console.WriteLine($"{"序号",-4} {"项目名称",-24} {"视频数",6} {"状态",-10} {"已完成",8} {"恢复来源",-10}");
        Console.WriteLine(new string('-', 88));

        var index = 1;
        foreach (var project in result.Projects.Where(project => !string.Equals(project.Status, "已完成", StringComparison.Ordinal)))
        {
            Console.WriteLine($"{index:00}   {TrimToWidth(project.DisplayName, 24),-24} {project.VideoCount,6} {project.Status,-10} {($"{project.CompletedSteps}/{project.TotalSteps}"),8} {project.ResumeFrom ?? "-", -10}");
            index++;
        }

        if (index == 1)
        {
            Console.WriteLine("无待处理项目。");
        }
    }

    private static string TrimToWidth(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : $"{value[..(maxLength - 1)]}…";
    }

    private static void WriteRunEvent(WorkRunEvent evt)
    {
        var projectColor = evt.Ok switch
        {
            true when string.Equals(evt.Kind, "project-skipped", StringComparison.Ordinal) => AnsiBlue,
            true => AnsiGreen,
            false => AnsiRed,
            _ => AnsiBlue
        };

        if (evt.Kind.StartsWith("project-", StringComparison.Ordinal))
        {
            Console.WriteLine($"{projectColor}{evt.DisplayName}{AnsiReset} {AnsiGray}{evt.Message}{AnsiReset}");
            return;
        }

        var statusColor = evt.Ok switch
        {
            false => AnsiRed,
            true when (evt.Message ?? string.Empty).Contains("跳过", StringComparison.Ordinal) => AnsiGray,
            true => AnsiWhite,
            _ => AnsiWhite
        };

        Console.WriteLine($"{projectColor}{evt.DisplayName}{AnsiReset} {AnsiCyan}[{GetStepDisplayName(evt.StepType ?? string.Empty)}]{AnsiReset} {statusColor}{evt.Message}{AnsiReset}");
    }

    private static void WriteRunSummary(WorkRunResult result, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine($"处理完成，耗时: {elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"输出目录: {result.RootDir}/workflow");
        Console.WriteLine($"成功 {result.SucceededProjects} 个，失败 {result.FailedProjects} 个，跳过 {result.SkippedProjects} 个");

        if (result.FailedProjects > 0)
        {
            Console.WriteLine($"{AnsiRed}失败项目：{AnsiReset}");
            foreach (var project in result.Projects.Where(item => !item.Ok))
            {
                Console.WriteLine($"{AnsiRed}- {project.DisplayName}{AnsiReset} {AnsiGray}{project.Message}{AnsiReset}");
            }
        }
    }

    private static string GetStepDisplayName(string type)
    {
        return type switch
        {
            "transcode" => "视频转码",
            "rewrite" => "仿写剧名简介",
            "poster-rename" => "生成海报图片",
            "project-image" => "生成工程图",
            "cost-report" => "生成成本报表图片",
            "batch-file-rename" => "重命名视频文件",
            _ => type
        };
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

    private const string AnsiReset = "\u001b[0m";
    private const string AnsiRed = "\u001b[31m";
    private const string AnsiGreen = "\u001b[32m";
    private const string AnsiBlue = "\u001b[34m";
    private const string AnsiCyan = "\u001b[36m";
    private const string AnsiWhite = "\u001b[37m";
    private const string AnsiGray = "\u001b[90m";
}
