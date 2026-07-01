using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShortDrama.Infrastructure.Automation;

public sealed class ExternalCliWeixinChannelUploader : IWeixinChannelUploader
{
    private static readonly string[] DefaultConfigNames =
    [
        "weixin-channel-submit.json",
        "weixin-channel-config.json",
        "weixin-channel-test-no-final-click.json",
        "weixin-channel-autogen.json"
    ];

    private readonly IExternalWeixinCliLocator _cliLocator;
    private readonly IWorkflowInteractionService _interactionService;
    private readonly ILogger<ExternalCliWeixinChannelUploader> _logger;

    public ExternalCliWeixinChannelUploader(
        IExternalWeixinCliLocator cliLocator,
        IWorkflowInteractionService interactionService,
        ILogger<ExternalCliWeixinChannelUploader> logger)
    {
        _cliLocator = cliLocator;
        _interactionService = interactionService;
        _logger = logger;
    }

    public async Task<WeixinUploadResult> UploadAsync(
        WeixinUploadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.ProjectDir))
        {
            return new WeixinUploadResult(false, request.ProjectDir, request.ConfigPath, "上传项目目录不存在。");
        }

        var cliCommand = await _cliLocator.ResolveAsync(cancellationToken);
        var resolvedConfigPath = ResolveConfigPath(request);
        var controlDirectory = CreateControlDirectory(request.ProjectKey);

        try
        {
            Directory.CreateDirectory(controlDirectory);
            progress?.Report($"微信上传：切换为外部 CLI -> {cliCommand.ScriptPath}");

            using var process = CreateProcess(cliCommand, request, controlDirectory);
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动外部微信上传进程: {cliCommand.FileName}");
            }

            progress?.Report($"微信上传：已启动外部进程 PID={process.Id}");
            using var cancellationRegistration = cancellationToken.Register(() => TryKillProcess(process));
            using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var stdoutTask = PumpReaderAsync(process.StandardOutput, stdout, progress, "stdout", cancellationToken);
            var stderrTask = PumpReaderAsync(process.StandardError, stderr, progress, "stderr", cancellationToken);
            var bridgeTask = Task.Run(
                () => BridgeInteractionAsync(
                    controlDirectory,
                    request,
                    progress,
                    bridgeCts.Token),
                CancellationToken.None);

            await process.WaitForExitAsync(CancellationToken.None);
            await bridgeCts.CancelAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await WaitForBridgeCompletionAsync(bridgeTask);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            resolvedConfigPath = ResolveConfigPath(request);
            var standardOutput = stdout.ToString();
            var standardError = stderr.ToString();
            var message = ExtractResultMessage(process.ExitCode, standardOutput, standardError);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "External Weixin CLI exited with code {ExitCode}. project={ProjectDir}, config={ConfigPath}",
                    process.ExitCode,
                    request.ProjectDir,
                    resolvedConfigPath);
            }

            return new WeixinUploadResult(
                Ok: process.ExitCode == 0,
                ProjectDir: request.ProjectDir,
                ConfigPath: resolvedConfigPath,
                Message: message);
        }
        finally
        {
            _interactionService.Clear();
            TryDeleteDirectory(controlDirectory);
        }
    }

    private static global::System.Diagnostics.Process CreateProcess(
        ExternalWeixinCliCommand cliCommand,
        WeixinUploadRequest request,
        string controlDirectory)
    {
        var arguments = new List<string>();
        arguments.AddRange(cliCommand.PrefixArguments);
        arguments.Add(cliCommand.ScriptPath);

        if (!string.IsNullOrWhiteSpace(request.ConfigPath))
        {
            arguments.Add("--config");
            arguments.Add(Path.GetFullPath(request.ConfigPath));
        }
        else
        {
            arguments.Add("--project-dir");
            arguments.Add(Path.GetFullPath(request.ProjectDir));
            if (!string.IsNullOrWhiteSpace(request.ConfigName))
            {
                arguments.Add("--config-name");
                arguments.Add(request.ConfigName);
            }
        }

        arguments.Add("--control-dir");
        arguments.Add(controlDirectory);

        var process = new global::System.Diagnostics.Process
        {
            StartInfo = new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = cliCommand.FileName,
                WorkingDirectory = cliCommand.ToolDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        return process.ConfigureArguments(arguments);
    }

    private async Task BridgeInteractionAsync(
        string controlDirectory,
        WeixinUploadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestPath = Path.Combine(controlDirectory, "request.json");
            var responsePath = Path.Combine(controlDirectory, "response.json");
            string? handledRequestId = null;
            var requestFileDetected = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!File.Exists(requestPath))
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!requestFileDetected)
                {
                    requestFileDetected = true;
                    progress?.Report($"微信上传：已检测到人工介入请求文件 {requestPath}");
                }

                ControlRequestPayload? payload;
                try
                {
                    await using var requestStream = File.Open(requestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    payload = await ReadControlRequestAsync(requestStream, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (IOException)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (payload is null || string.IsNullOrWhiteSpace(payload.RequestId))
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(handledRequestId, payload.RequestId, StringComparison.Ordinal))
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                handledRequestId = payload.RequestId;
                progress?.Report($"微信上传：已读取人工介入请求 {payload.RequestId}");
                var options = payload.Options?
                    .Where(option => !string.IsNullOrWhiteSpace(option))
                    .Select(option => option.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray() ?? ["stop"];

                var message = string.IsNullOrWhiteSpace(payload.Message)
                    ? "等待人工处理。"
                    : payload.Message.Trim();
                progress?.Report($"微信上传：等待人工处理 -> {message}");

                string decision;
                try
                {
                    decision = await _interactionService.RequestDecisionAsync(
                        new WorkflowInteractionRequest(
                            payload.RequestId,
                            request.ProjectKey,
                            ResolveDisplayName(request),
                            "weixin-upload",
                            string.IsNullOrWhiteSpace(payload.Scope) ? "project" : payload.Scope.Trim(),
                            string.IsNullOrWhiteSpace(payload.Stage) ? "takeover" : payload.Stage.Trim(),
                            message,
                            options),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    decision = "stop";
                }

                await File.WriteAllTextAsync(
                    responsePath,
                    JsonSerializer.Serialize(
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["requestId"] = payload.RequestId,
                            ["action"] = decision
                        },
                        JsonOptions),
                    Encoding.UTF8,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Report($"微信上传：人工介入桥接异常 -> {ex.Message}");
            throw;
        }
    }

    private static async Task PumpReaderAsync(
        StreamReader reader,
        StringBuilder buffer,
        IProgress<string>? progress,
        string _,
        CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            buffer.AppendLine(line);
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            progress?.Report(text);
        }
    }

    private static async Task WaitForBridgeCompletionAsync(Task bridgeTask)
    {
        try
        {
            await bridgeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation while shutting down.
        }
    }

    private static string ResolveDisplayName(WeixinUploadRequest request)
    {
        return string.IsNullOrWhiteSpace(request.DisplayName)
            ? Path.GetFileName(Path.GetFullPath(request.ProjectDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : request.DisplayName;
    }

    private static string CreateControlDirectory(string projectKey)
    {
        var safeProjectKey = string.IsNullOrWhiteSpace(projectKey)
            ? "project"
            : string.Concat(projectKey.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(Path.GetTempPath(), $"shortdrama-weixin-control-{safeProjectKey}-{Guid.NewGuid():N}");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private static void TryKillProcess(global::System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore termination failures.
        }
    }

    private static string? ResolveConfigPath(WeixinUploadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConfigPath))
        {
            return Path.GetFullPath(request.ConfigPath);
        }

        foreach (var name in EnumerateConfigCandidates(request.ConfigName))
        {
            var path = Path.Combine(request.ProjectDir, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateConfigCandidates(string? configName)
    {
        if (!string.IsNullOrWhiteSpace(configName))
        {
            yield return configName;
        }

        foreach (var name in DefaultConfigNames)
        {
            if (string.Equals(name, configName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return name;
        }
    }

    private static string ExtractResultMessage(int exitCode, string standardOutput, string standardError)
    {
        var outputLines = EnumerateNonEmptyLines(standardOutput).ToArray();
        var errorLines = EnumerateNonEmptyLines(standardError).ToArray();
        if (exitCode == 0)
        {
            return outputLines.LastOrDefault() ?? "微信上传执行完成。";
        }

        return errorLines.LastOrDefault()
            ?? outputLines.LastOrDefault()
            ?? $"微信上传执行失败，退出码 {exitCode}。";
    }

    private static IEnumerable<string> EnumerateNonEmptyLines(string value)
    {
        return value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static async Task<ControlRequestPayload?> ReadControlRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var requestId = GetString(root, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var options = root.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array
            ? optionsElement
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray()
            : null;

        return new ControlRequestPayload(
            requestId,
            GetString(root, "stage"),
            GetString(root, "scope"),
            GetString(root, "message"),
            options);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record ControlRequestPayload(
        string RequestId,
        string? Stage,
        string? Scope,
        string? Message,
        string[]? Options);
}

internal static class ProcessExtensions
{
    public static global::System.Diagnostics.Process ConfigureArguments(
        this global::System.Diagnostics.Process process,
        IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }
}
