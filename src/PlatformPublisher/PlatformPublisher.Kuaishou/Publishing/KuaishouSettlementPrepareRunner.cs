using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouSettlementPrepareRunner
{
    public static async Task<KuaishouSettlementPrepareResult> RunAsync(
        string projectDirectory,
        KuaishouPersonalConfig config,
        KuaishouSettlementPrepareOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<KuaishouPrepareStepEvent, Task>? onStep = null)
    {
        var script = ResolveBridgeScript() ?? throw new InvalidOperationException("未找到快手分账准备脚本。");
        var toolRoot = ResolveAssistantRoot() ?? throw new InvalidOperationException("未找到短剧助手，无法按快手分账步骤生成产物。");
        var python = ResolveAssistantPython();
        var enterprise = config.StoragePlatform == PlatformPublisher.Common.Models.PublishPlatform.KuaishouEnterpriseRevenue;
        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = toolRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in new[]
                 {
                     "-X", "utf8", script,
                     "--project", projectDirectory,
                     "--tool-root", toolRoot,
                     "--edition", enterprise ? "enterprise" : "personal",
                 })
            start.ArgumentList.Add(argument);
        var workspace = ResolveWorkspace(projectDirectory);
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(workspace);
        }
        AddOption(start, "--template-id", options.TemplateId);
        AddOption(start, "--template-root", options.TemplateRoot);
        if (options.Download) start.ArgumentList.Add("--download");
        if (options.Rewrite) start.ArgumentList.Add("--rewrite");
        if (options.Poster) start.ArgumentList.Add("--poster");
        if (options.ProjectImages) start.ArgumentList.Add("--project-images");
        if (options.AutoFill) start.ArgumentList.Add("--auto-fill");
        if (options.Force) start.ArgumentList.Add("--force");
        var overlay = Path.Combine(Path.GetTempPath(), "ks-config-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(
            overlay,
            System.Text.Json.JsonSerializer.Serialize(KuaishouPersonalFormResolver.SettingsOverlay(config, enterprise)),
            cancellationToken);
        start.ArgumentList.Add("--overrides");
        start.ArgumentList.Add(overlay);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动短剧助手。");
        var error = new StringBuilder();
        var stdoutError = "";
        KuaishouSettlementPrepareResult? result = null;
        var stderrTask = ReadStderrAsync(process, error, cancellationToken);
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (line.StartsWith("LOG\t", StringComparison.Ordinal))
                    progress?.Report(line[4..]);
                else if (line.StartsWith("STEP\t", StringComparison.Ordinal))
                {
                    if (onStep is not null && TryParseStepEvent(line, out var step))
                        await onStep(step);
                }
                else if (line.StartsWith("ERROR\t", StringComparison.Ordinal))
                    stdoutError = line[6..];
                else if (line.StartsWith("RESULT\t", StringComparison.Ordinal))
                    result = ParseResult(line[7..]);
            }

            await process.WaitForExitAsync(cancellationToken);
            await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* 停止时进程可能已经退出。 */ }
            throw;
        }
        finally
        {
            try { File.Delete(overlay); } catch { /* 临时覆盖文件删除失败不影响结果。 */ }
        }

        if (process.ExitCode != 0 || result is null)
        {
            var message = FirstNonEmpty(stdoutError, error.ToString().Trim(), "短剧助手快手分账准备失败。");
            throw new InvalidOperationException(message);
        }

        return result;
    }

    private static async Task ReadStderrAsync(Process process, StringBuilder error, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!string.IsNullOrWhiteSpace(line)) error.AppendLine(line);
        }
    }

    public static bool TryParseStepEvent(string line, out KuaishouPrepareStepEvent step)
    {
        step = default;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("STEP\t", StringComparison.Ordinal))
            return false;
        var parts = line.Split('\t', 4);
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]))
            return false;
        step = new KuaishouPrepareStepEvent(parts[1].Trim(), parts[2].Trim(), parts.Length > 3 ? parts[3].Trim() : "");
        return true;
    }

    private static KuaishouSettlementPrepareResult ParseResult(string json)
    {
        var node = JsonNode.Parse(json);
        var steps = node?["completed_prepare_steps"]?.AsArray()
            .Select(item => item?.GetValue<string>() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray() ?? [];
        return new KuaishouSettlementPrepareResult(steps, node?["episode_count"]?.GetValue<int>() ?? 0);
    }

    private static void AddOption(ProcessStartInfo start, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string? ResolveBridgeScript()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tools", "kuaishou", "kuaishou_prepare_bridge.py");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }

    private static string? ResolveAssistantRoot()
    {
        var configured = Environment.GetEnvironmentVariable("WEIXIN_CHANNEL_TOOL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "modules", "kuaishou", "upload_service.py")))
            return configured;
        const string local = @"D:\code\weixin-channel-tool";
        return File.Exists(Path.Combine(local, "modules", "kuaishou", "upload_service.py")) ? local : null;
    }

    private static string ResolveAssistantPython()
    {
        var venv = @"D:\code\weixin-channel-tool\.venv\Scripts\python.exe";
        return File.Exists(venv) ? venv : "python";
    }

    private static string? ResolveWorkspace(string projectDirectory)
    {
        var directory = new DirectoryInfo(projectDirectory);
        if (directory.Parent?.Name.Equals("workflow", StringComparison.OrdinalIgnoreCase) == true)
            return directory.Parent.Parent?.FullName;
        return directory.Parent?.FullName;
    }
}
