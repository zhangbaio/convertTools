using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Weixin.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.DependencyInjection;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: PreUploadSmoke <template-workflow-dir> <test-source-dir> <test-workflow-dir>");
    return 2;
}

var templateDir = Path.GetFullPath(args[0]);
var sourceDir = Path.GetFullPath(args[1]);
var workflowDir = Path.GetFullPath(args[2]);
var skipSmartRecut = args.Skip(3).Any(value => string.Equals(value, "--skip-smart-recut", StringComparison.OrdinalIgnoreCase));
var resumeAfterPoster = args.Skip(3).Any(value => string.Equals(value, "--resume-after-poster", StringComparison.OrdinalIgnoreCase));
var skipProofs = args.Skip(3).Any(value => string.Equals(value, "--skip-proofs", StringComparison.OrdinalIgnoreCase));
var skipRemux = args.Skip(3).Any(value => string.Equals(value, "--skip-remux", StringComparison.OrdinalIgnoreCase));
var rerunRewrite = args.Skip(3).Any(value => string.Equals(value, "--rerun-rewrite", StringComparison.OrdinalIgnoreCase));
var downloadOnly = args.Skip(3).Any(value => string.Equals(value, "--download-only", StringComparison.OrdinalIgnoreCase));
var preferHgnew = args.Skip(3).Any(value => string.Equals(value, "--prefer-hgnew", StringComparison.OrdinalIgnoreCase));
var preferHghigh = args.Skip(3).Any(value => string.Equals(value, "--prefer-hghigh", StringComparison.OrdinalIgnoreCase));
var smartOnly = args.Skip(3).Any(value => string.Equals(value, "--smart-only", StringComparison.OrdinalIgnoreCase));
foreach (var path in new[] { templateDir, sourceDir, workflowDir })
    if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

var logPath = Path.Combine(workflowDir, "preupload-smoke.log");
var reportPath = Path.Combine(workflowDir, "preupload-smoke-report.json");
var logs = new List<string>();
var stepFailures = new List<string>();
var logGate = new object();
void Log(string message)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}";
    lock (logGate)
    {
        logs.Add(line);
        Console.WriteLine(line);
        File.AppendAllLines(logPath, [line]);
    }
}

File.WriteAllText(logPath, string.Empty);
var metadata = ReadObject(Path.Combine(sourceDir, "shortdrama-project.json"));
var title = metadata["title"]?.GetValue<string>() ?? Path.GetFileName(sourceDir);
var bookId = metadata["bookId"]?.GetValue<string>();
var episodes = metadata["episodes"]?.GetValue<string>() ?? "all";
var quality = metadata["quality"]?.GetValue<string>() ?? "1080P";
var concurrent = metadata["concurrent"]?.GetValue<int>() ?? 3;
var episodeNumberMode = metadata["episodeNumberMode"]?.GetValue<string>() ?? "source";

var platformSettings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
var mainSettings = ClientSettingsStore.Load();
var settings = HasDownloadConfiguration(platformSettings) ? platformSettings : mainSettings;
if (preferHgnew && Present(mainSettings.HgnewAccount, mainSettings.HgnewPassword, mainSettings.HgnewUdid))
{
    settings = mainSettings.Clone();
    settings.DramaSourceChain = "hgnew";
}
if (preferHghigh && Present(mainSettings.HghighAccount, mainSettings.HghighPassword, mainSettings.HghighDeviceId))
{
    settings = mainSettings.Clone();
    settings.DramaSourceChain = "hghigh";
}
Log(ReferenceEquals(settings, platformSettings)
    ? $"下载配置来源：多平台独立设置（{settings.DramaSourceChain}）"
    : $"下载配置来源：现有主设置回退（{settings.DramaSourceChain}）；多平台独立设置缺少当前下载源凭据");
ShortDramaDramaServices.RefreshSettings(settings);
var projectConfigPath = PrepareProjectConfig(sourceDir, workflowDir, settings);
DramaDownloadResult download;
if (resumeAfterPoster)
{
    var count = Directory.EnumerateFiles(sourceDir, "*.mp4", SearchOption.TopDirectoryOnly).Count();
    download = new DramaDownloadResult(true, sourceDir, count, "复用已完成下载");
    Log($"下载：复用 {count} 集");
}
else
{
    download = await ShortDramaDramaServices.Downloader.DownloadAsync(
        new DramaDownloadRequest(sourceDir, sourceDir, title, bookId, episodes, quality, concurrent, episodeNumberMode),
        new InlineProgress<string>(Log),
        CancellationToken.None);
    if (!download.Ok) throw new InvalidOperationException(download.Message ?? "下载失败。");
    Log($"下载完成：{download.VideoCount} 集");
}
if (downloadOnly)
{
    var samplePath = Directory.EnumerateFiles(sourceDir, "*.mp4", SearchOption.TopDirectoryOnly).OrderBy(EpisodeIndex).FirstOrDefault();
    var sample = samplePath is null ? null : await ProbeVideoAsync(samplePath);
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
    {
        generatedAt = DateTimeOffset.Now,
        downloadOnly = true,
        source = settings.DramaSourceChain,
        download,
        sample,
        ok = download.Ok && sample is { BitRate: >= 800_000 },
    }, new JsonSerializerOptions { WriteIndented = true }));
    Log($"仅下载报告：{reportPath}");
    return download.Ok && sample is { BitRate: >= 800_000 } ? 0 : 1;
}

var services = new ServiceCollection();
services.AddLogging();
services.AddShortDramaServices();
using var provider = services.BuildServiceProvider();
var work = provider.GetRequiredService<IWorkService>();
var validation = provider.GetRequiredService<IMaterialValidationService>();
var workProgress = new InlineProgress<WorkRunEvent>(item =>
{
    if (!string.IsNullOrWhiteSpace(item.Message)) Log($"{item.DisplayName} | {item.Message}");
});
var runtimeConfigPath = projectConfigPath;

async Task RunStep(string key, string label)
{
    Log($"开始：{label}");
    var result = await work.RunProjectStepAsync(
        sourceDir, null, key, true, workProgress, CancellationToken.None, runtimeConfigPath);
    if (!result.Ok) throw new InvalidOperationException(result.Message ?? $"{label}失败。");
    Log($"完成：{label}");
}

if (resumeAfterPoster)
{
    Log("复用已完成的素材转码、改写信息、海报和字段补齐");
}
else if (skipSmartRecut)
{
    Log("智能重剪：本轮已跳过实际渲染，改用素材转码继续验证其余预上传步骤");
    await RunStep("transcode", "素材转码");
}
else
{
    var smartRecut = new WeixinSmartRecutService(new SettingsAiProvider(settings, disableLlmScore: true), work);
    Log("开始：智能重剪");
    var smartResult = await smartRecut.RunAsync(
        sourceDir,
        outputEpisodeCount: 0,
        minSeconds: 60,
        maxSeconds: 180,
        force: true,
        new InlineProgress<string>(Log),
        CancellationToken.None);
    Log($"完成：智能重剪，输出 {smartResult.OutputVideos.Count} 集");
    if (smartOnly)
    {
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now,
            smartRecutOnly = true,
            requested = Directory.EnumerateFiles(sourceDir, "*.mp4", SearchOption.TopDirectoryOnly).Count(),
            actual = smartResult.OutputVideos.Count,
            outputs = smartResult.OutputVideos.Select(Path.GetFileName),
            ok = smartResult.OutputVideos.Count == Directory.EnumerateFiles(sourceDir, "*.mp4", SearchOption.TopDirectoryOnly).Count(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Log($"智能重剪报告：{reportPath}");
        return 0;
    }
}

if (!resumeAfterPoster || rerunRewrite)
{
    await RunStep("rewrite", "改写信息");
}
if (!resumeAfterPoster)
{
    await RunStep("poster-rename", "生成海报");
}
var autoFill = await work.AutoFillProjectInfoAsync(sourceDir, null, CancellationToken.None);
Log($"完成：补齐字段（{string.Join('、', autoFill.UpdatedFields)}）");
await RunStep("cost-report", "生成成本报表");
await RunStep("project-image", "生成工程图");

var proof = new WeixinProofArtifactsService(work);
var job = new PublishJob
{
    ProjectDirectory = sourceDir,
    ProjectName = Path.GetFileName(workflowDir).TrimStart('_'),
    AccountName = "preupload-smoke",
};
if (!skipProofs) try
{
    await proof.GenerateAiProofAsync(job, settings, true, new InlineProgress<string>(Log), CancellationToken.None);
}
catch (Exception ex)
{
    stepFailures.Add($"AI制作证明：{ex.Message}");
    Log($"失败：AI制作证明：{ex.Message}");
}
if (!skipProofs) try
{
    await proof.GenerateTimestampCertificateAsync(job, settings, true, new InlineProgress<string>(Log), CancellationToken.None);
}
catch (Exception ex)
{
    stepFailures.Add($"可信时间戳：{ex.Message}");
    Log($"失败：可信时间戳：{ex.Message}");
}

if (!skipRemux)
{
    var remux = await work.RemuxUploadVideosAsync(sourceDir, null, workProgress, CancellationToken.None);
    if (!remux.Ok) throw new InvalidOperationException(remux.Message);
    Log($"完成：无损重封装，处理 {remux.RemuxedFiles}，跳过 {remux.SkippedFiles}");
}
var configPath = await work.EnsureWeixinUploadConfigAsync(sourceDir, null, CancellationToken.None);
var actualWorkflowDir = Path.GetDirectoryName(configPath) ?? workflowDir;
var validationResult = await validation.ValidateAsync(actualWorkflowDir, CancellationToken.None);
var expectedTestSuffix = "_codex测试";
if (Path.GetFileName(actualWorkflowDir).TrimStart('_').EndsWith(expectedTestSuffix, StringComparison.Ordinal))
{
    var ignored = validationResult.Issues.Where(item => item.Code == "workflow-title-mismatch").ToArray();
    if (ignored.Length > 0) Log("素材校验[测试隔离提示] workflow 目录附加了 _codex测试 后缀，正式目录不受影响");
    validationResult = new MaterialValidationResult(validationResult.Issues.Except(ignored).ToArray());
}
foreach (var issue in validationResult.Issues) Log($"素材校验[{issue.Severity}] {issue.Code}: {issue.Message}");

if (validationResult.HasErrors)
{
    var repairKeys = validationResult.Issues.Where(item => item.CanAutoFix).Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
    var repairSteps = new List<(bool Needed, string Key, string Label)>
    {
        (repairKeys.Contains("info-missing") || repairKeys.Contains("info-invalid"), "rewrite", "修复短剧信息"),
        (repairKeys.Contains("poster-missing"), "poster-rename", "修复海报"),
        (repairKeys.Contains("project-images-missing"), "project-image", "修复工程图"),
        (repairKeys.Contains("cost-missing"), "cost-report", "修复成本报表"),
        (repairKeys.Contains("video-title-mismatch"), "batch-file-rename", "修复视频命名"),
    };
    foreach (var item in repairSteps.Where(item => item.Needed)) await RunStep(item.Key, item.Label);
    if (repairKeys.Contains("weixin-upload-config-missing"))
        await work.EnsureWeixinUploadConfigAsync(sourceDir, null, CancellationToken.None);
    if (repairKeys.Contains("weixin-title-mismatch"))
        await work.RefreshWeixinConfigsAsync(sourceDir, null, CancellationToken.None);
    validationResult = await validation.ValidateAsync(actualWorkflowDir, CancellationToken.None);
}

var template = await SnapshotAsync(templateDir);
var actual = await SnapshotAsync(actualWorkflowDir);
var differences = Compare(template, actual, validationResult);
var downloadSamplePath = Directory.EnumerateFiles(sourceDir, "*.mp4", SearchOption.TopDirectoryOnly).OrderBy(EpisodeIndex).FirstOrDefault();
var downloadSample = downloadSamplePath is null ? null : await ProbeVideoAsync(downloadSamplePath);
if (downloadSample is not null && downloadSample.BitRate > 0 && downloadSample.BitRate < 800_000)
    differences.Add(new Difference("error", "download-bitrate",
        $"下载样本码率仅 {downloadSample.BitRate / 1_000_000d:0.00} Mbps，低于预上传最低 0.80 Mbps"));
var report = new
{
    generatedAt = DateTimeOffset.Now,
    templateDirectory = templateDir,
    testSourceDirectory = sourceDir,
    testWorkflowDirectory = actualWorkflowDir,
    uploadExecuted = false,
    download = new { download.Ok, download.VideoCount, download.Message },
    downloadSample,
    template,
    actual,
    validationIssues = validationResult.Issues.Select(item => new { item.Severity, item.Code, item.Message, item.CanAutoFix }),
    stepFailures,
    differences,
    ok = stepFailures.Count == 0 && !validationResult.HasErrors && differences.All(item => item.Level != "error"),
};
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Log($"报告：{reportPath}");
return report.ok ? 0 : 1;

static JsonObject ReadObject(string path) =>
    JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidOperationException($"JSON 无效：{path}");

static bool HasDownloadConfiguration(ClientSettings value) =>
    (value.DramaSourceChain ?? "hgnew").Trim().ToLowerInvariant() switch
    {
        "hgnew" => Present(value.HgnewAccount, value.HgnewPassword, value.HgnewUdid),
        "hghigh" => Present(value.HghighAccount, value.HghighPassword, value.HghighDeviceId),
        "mapleleaf" => Present(value.MapleleafAccount, value.MapleleafPassword, value.MapleleafUdid),
        "pikachu" => !string.IsNullOrWhiteSpace(value.PikachuFanqieCookie) || !string.IsNullOrWhiteSpace(value.PikachuDeviceId),
        "hglocal" => !string.IsNullOrWhiteSpace(value.HongguoLocalBaseUrl),
        _ => false,
    };

static bool Present(params string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value));

static string PrepareProjectConfig(string sourceDirectory, string workflowDirectory, ClientSettings settings)
{
    var root = Directory.GetParent(sourceDirectory)?.FullName
               ?? throw new InvalidOperationException("无法确定测试工作根目录");
    var configDir = Path.Combine(root, "config");
    Directory.CreateDirectory(configDir);
    var tempConfig = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
    var payload = ReadObject(tempConfig);
    var info = ParseInfo(Path.Combine(workflowDirectory, "短剧信息.txt"));
    var company = info.GetValueOrDefault("制作公司", string.Empty);

    var legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".weixin_channel_tool",
        "settings.json");
    if (!File.Exists(legacyPath)) throw new FileNotFoundException("未找到参考项目旧设置，无法准备成本报表资源。", legacyPath);
    var legacy = ReadObject(legacyPath);
    var profilesText = legacy["account_profiles_json"]?.GetValue<string>() ?? "[]";
    var profiles = JsonNode.Parse(profilesText)?.AsArray() ?? [];
    JsonObject? selected = profiles
        .OfType<JsonObject>()
        .FirstOrDefault(profile => string.Equals(profile["cost_report_company_name"]?.ToString(), company, StringComparison.Ordinal));
    selected ??= profiles.OfType<JsonObject>().FirstOrDefault(profile =>
        Existing(profile["cost_report_sign_path"]?.ToString()) && Existing(profile["cost_report_seal_path"]?.ToString()));
    if (selected is null) throw new InvalidOperationException("旧账号配置中没有可用的成本报表签名和印章。");

    var signSource = selected["cost_report_sign_path"]?.ToString() ?? string.Empty;
    var sealSource = selected["cost_report_seal_path"]?.ToString() ?? string.Empty;
    if (!Existing(signSource) || !Existing(sealSource)) throw new FileNotFoundException("成本报表签名或印章文件不存在。");
    File.Copy(signSource, Path.Combine(configDir, "sign.png"), true);
    File.Copy(sealSource, Path.Combine(configDir, "seal.png"), true);

    var templateSource = selected["cost_report_template_docx_path"]?.ToString();
    if (!Existing(templateSource))
        templateSource = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "shortdrama-assistant", "_internal", "成本报表模板示例.docx"),
            @"D:\Program Files\shortdrama-assistant\_internal\成本报表模板示例.docx",
            @"D:\code\weixin-channel-tool\成本报表模板示例.docx",
        }.FirstOrDefault(Existing);
    if (!Existing(templateSource)) throw new FileNotFoundException("成本报表模板不存在。", templateSource);
    var templateTarget = Path.Combine(configDir, "成本报表模板.docx");
    File.Copy(templateSource!, templateTarget, true);

    payload["CompanyName"] = company;
    payload["TemplateDocxPath"] = templateTarget;
    payload["CostReportTemplatePath"] = templateTarget;
    payload["CostReportActorPayRatio"] = selected["cost_report_actor_pay_ratio"]?.DeepClone();
    payload["CostReportLegalRepresentative"] = selected["cost_report_legal_representative"]?.DeepClone();
    payload["CostReportDate"] = selected["cost_report_date"]?.DeepClone();
    var configPath = Path.Combine(configDir, "config.json");
    File.WriteAllText(configPath, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return configPath;
}

static bool Existing(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

static async Task<ProjectSnapshot> SnapshotAsync(string directory)
{
    var videos = Directory.Exists(Path.Combine(directory, "videos"))
        ? Directory.EnumerateFiles(Path.Combine(directory, "videos"), "*.mp4").OrderBy(EpisodeIndex).ToArray()
        : [];
    var info = ParseInfo(Path.Combine(directory, "短剧信息.txt"));
    var artifactCandidates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["短剧信息"] = ["短剧信息.txt"],
        ["海报图片"] = ["海报图片.png", "海报图片.jpg", "海报图片.jpeg"],
        ["成本报表"] = ["成本报表.png"],
        ["工程图_1"] = ["工程图_1.png"],
        ["工程图_2"] = ["工程图_2.png"],
        ["工程图_3"] = ["工程图_3.png"],
        ["工程图_4"] = ["工程图_4.png"],
        ["AI制作证明_1"] = ["AI制作证明_1.png"],
        ["AI制作证明_2"] = ["AI制作证明_2.png"],
        ["AI制作证明_3"] = ["AI制作证明_3.png"],
        ["AI制作证明_4"] = ["AI制作证明_4.png"],
        ["可信时间戳"] = ["可信时间戳认证证书.pdf"],
        ["视频号配置"] = ["weixin-channel-autogen.json", "weixin-channel.json"],
    };
    var files = artifactCandidates.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.Select(name => Path.Combine(directory, name)).Where(File.Exists).Select(path => new FileInfo(path).Length).FirstOrDefault(),
        StringComparer.OrdinalIgnoreCase);
    var sample = videos.FirstOrDefault();
    var probe = sample is null ? null : await ProbeVideoAsync(sample);
    return new ProjectSnapshot(
        directory,
        info.GetValueOrDefault("原剧名", string.Empty),
        info.GetValueOrDefault("新剧名", string.Empty),
        ParseInt(info.GetValueOrDefault("集数")),
        videos.Length,
        videos.Sum(path => new FileInfo(path).Length),
        probe,
        files);
}

static List<Difference> Compare(ProjectSnapshot template, ProjectSnapshot actual, dynamic validation)
{
    var differences = new List<Difference>();
    foreach (var pair in actual.ArtifactBytes)
        if (pair.Value <= 0) differences.Add(new Difference("error", pair.Key, "测试产物缺失或为空"));
    if (actual.InfoEpisodeCount != actual.VideoCount)
        differences.Add(new Difference("error", "episode-count", $"短剧信息集数 {actual.InfoEpisodeCount} 与 videos 数量 {actual.VideoCount} 不一致"));
    if (template.VideoCount != actual.VideoCount)
        differences.Add(new Difference("info", "template-video-count", $"模板 {template.VideoCount} 集，当前重跑 {actual.VideoCount} 集；需结合原始下载集数判断"));
    if (!string.Equals(template.OriginalTitle, actual.OriginalTitle, StringComparison.Ordinal))
        differences.Add(new Difference("error", "original-title", $"原剧名不一致：模板={template.OriginalTitle}，当前={actual.OriginalTitle}"));
    if (actual.SampleVideo is null || actual.SampleVideo.Width <= 0 || actual.SampleVideo.Height <= 0)
        differences.Add(new Difference("error", "video-probe", "无法读取测试视频尺寸"));
    return differences;
}

static Dictionary<string, string> ParseInfo(string path)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return result;
    foreach (var line in File.ReadLines(path))
    {
        var index = line.IndexOfAny([':', '：']);
        if (index > 0) result[line[..index].Trim()] = line[(index + 1)..].Trim();
    }
    return result;
}

static int ParseInt(string? value)
{
    var match = Regex.Match(value ?? string.Empty, @"\d+");
    return match.Success && int.TryParse(match.Value, out var result) ? result : 0;
}

static int EpisodeIndex(string path)
{
    var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"第\s*(\d+)\s*集");
    return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : int.MaxValue;
}

static async Task<VideoProbe?> ProbeVideoAsync(string path)
{
    var start = new ProcessStartInfo("ffprobe")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,duration,bit_rate", "-of", "json", path })
        start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 ffprobe");
    var json = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) return null;
    var stream = JsonNode.Parse(json)?["streams"]?.AsArray().FirstOrDefault()?.AsObject();
    return stream is null ? null : new VideoProbe(
        stream["width"]?.GetValue<int>() ?? 0,
        stream["height"]?.GetValue<int>() ?? 0,
        double.TryParse(stream["duration"]?.ToString(), out var duration) ? duration : 0,
        long.TryParse(stream["bit_rate"]?.ToString(), out var bitRate) ? bitRate : 0);
}

sealed class SettingsAiProvider(ClientSettings settings, bool disableLlmScore = false) : IAiRuntimeSettingsProvider
{
    public AiRuntimeSettings Load() => new(
        disableLlmScore ? string.Empty : settings.AiTextEndpoint,
        settings.AiTextApiKey,
        disableLlmScore ? string.Empty : settings.AiTextModel,
        settings.AiTextTimeoutSeconds,
        settings.TiktokAsrLocalModelDir,
        settings.TiktokAsrLocalVadPath,
        settings.TiktokAsrAppId,
        settings.TiktokAsrAccessToken,
        settings.TiktokAsrLanguage);
}

sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

sealed record ProjectSnapshot(
    string Directory,
    string OriginalTitle,
    string NewTitle,
    int InfoEpisodeCount,
    int VideoCount,
    long VideoBytes,
    VideoProbe? SampleVideo,
    IReadOnlyDictionary<string, long> ArtifactBytes);

sealed record VideoProbe(int Width, int Height, double DurationSeconds, long BitRate);
sealed record Difference(string Level, string Item, string Message);
