using System.Text.Json;
using ShortDrama.Core.Interfaces;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>桥接完整 Python 剪辑引擎：定位 weixin-channel-tool 的 clip_runner.py，用系统 Python 跑，
/// 复用其已装好的重依赖（sherpa-onnx/scenedetect/edge-tts…）与用户 ~/.weixin_channel_tool/settings.json。
/// 产物落到 &lt;project_dir&gt;/素材剪辑输出/&lt;模式&gt;/，可被 material_clips 发布来源消费。</summary>
public sealed class PythonClipEngineRunner
{
    private const string ToolDirectoryName = "weixin-channel-tool";
    private const string ScriptName = "clip_runner.py";

    private readonly PythonToolResolver _python;
    private readonly IExternalProcessRunner _processRunner;

    public PythonClipEngineRunner(PythonToolResolver python, IExternalProcessRunner processRunner)
    {
        _python = python;
        _processRunner = processRunner;
    }

    public async Task<ClipEngineResult> GenerateAsync(ClipEngineRequest request, CancellationToken ct)
    {
        var python = await _python.ResolvePythonCommandAsync(ct);
        var toolDir = _python.ResolveRepositoryToolDirectory(ToolDirectoryName);
        var scriptPath = Path.Combine(toolDir, ScriptName);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"未找到剪辑引擎入口：{scriptPath}", scriptPath);

        // 请求写临时 JSON（键名与 clip_runner.py 对齐：snake_case）。
        var payload = new Dictionary<string, object?>
        {
            ["project_dir"] = request.ProjectDir,
            ["video_paths"] = request.VideoPaths,
            ["modes"] = request.Modes.Count > 0 ? request.Modes : new[] { "highlight" },
            ["settings_overrides"] = request.SettingsOverrides ?? new Dictionary<string, object>(),
        };
        var reqFile = Path.Combine(Path.GetTempPath(), $"clip-req-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(reqFile, JsonSerializer.Serialize(payload), ct);

        try
        {
            var args = new List<string>(python.PrefixArguments) { scriptPath, reqFile };
            var result = await _processRunner.RunAsync(python.FileName, args, toolDir, ct);
            return Parse(result.StandardOutput, result.StandardError, result.ExitCode);
        }
        finally
        {
            try { File.Delete(reqFile); } catch { /* 清理失败忽略 */ }
        }
    }

    private static ClipEngineResult Parse(string stdout, string stderr, int exitCode)
    {
        // clip_runner 只往 stdout 打单行 JSON 结果，日志走 stderr；取最后一非空行做结果。
        var line = (stdout ?? "")
            .Split('\n')
            .Select(s => s.Trim())
            .LastOrDefault(s => s.Length > 0 && s.StartsWith("{"));

        if (line is null)
            return new ClipEngineResult(false, Array.Empty<string>(), null,
                $"剪辑引擎无有效输出（退出码 {exitCode}）：{Trim(stderr)}", stderr ?? "");

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var outputs = ReadStringArray(root, "outputs");
            var byMode = ReadByMode(root);
            string? error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : null;
            return new ClipEngineResult(ok, outputs, byMode, error, stderr ?? "");
        }
        catch (Exception ex)
        {
            return new ClipEngineResult(false, Array.Empty<string>(), null,
                $"解析剪辑引擎结果失败：{ex.Message}", stderr ?? "");
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return list;
    }

    private static IReadOnlyDictionary<string, List<string>>? ReadByMode(JsonElement root)
    {
        if (!root.TryGetProperty("byMode", out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, List<string>>();
        foreach (var prop in el.EnumerateObject())
        {
            var list = new List<string>();
            if (prop.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in prop.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            map[prop.Name] = list;
        }
        return map;
    }

    private static string Trim(string? s)
    {
        s = (s ?? "").Trim();
        return s.Length <= 300 ? s : s[^300..];
    }
}

/// <summary>剪辑引擎请求。SettingsOverrides 覆盖 GlobalSettings 的 material_clip_* 字段（可空，空则用用户既有配置）。</summary>
public sealed record ClipEngineRequest(
    string ProjectDir,
    IReadOnlyList<string> VideoPaths,
    IReadOnlyList<string> Modes,
    IReadOnlyDictionary<string, object>? SettingsOverrides = null);

/// <summary>剪辑引擎结果。Outputs=全部产出路径；ByMode=按模式分组；Log=stderr 过程日志。</summary>
public sealed record ClipEngineResult(
    bool Ok,
    IReadOnlyList<string> Outputs,
    IReadOnlyDictionary<string, List<string>>? ByMode,
    string? Error,
    string Log);
