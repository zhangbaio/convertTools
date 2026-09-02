using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.DependencyInjection;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: KuaishouPersonalSmoke <template-workflow-dir> <isolated-test-root> [--reuse-download]");
    return 2;
}

var template = Path.GetFullPath(args[0]);
var testRoot = Path.GetFullPath(args[1]);
var reuseDownload = args.Contains("--reuse-download", StringComparer.OrdinalIgnoreCase);
if (!Directory.Exists(template)) throw new DirectoryNotFoundException(template);
Directory.CreateDirectory(testRoot);
var source = Path.Combine(testRoot, "source");
var workflow = Path.Combine(testRoot, "workflow", "_中奖后带母住深山_codex测试");
Directory.CreateDirectory(source);
var isolatedMetadataPath = Path.Combine(source, "shortdrama-project.json");
if (reuseDownload && File.Exists(isolatedMetadataPath))
    workflow = ReadObject(isolatedMetadataPath)["workflowProjectDir"]?.ToString() ?? workflow;
Directory.CreateDirectory(workflow);
Directory.CreateDirectory(Path.Combine(workflow, "videos"));
var logPath = Path.Combine(testRoot, "kuaishou-personal-smoke.log");
var reportPath = Path.Combine(testRoot, "kuaishou-personal-smoke-report.json");
File.WriteAllText(logPath, string.Empty);
var logGate = new object();
void Log(string value)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {value.Trim()}";
    lock (logGate)
    {
        Console.WriteLine(line);
        File.AppendAllLines(logPath, [line]);
    }
}

var templateMetadata = reuseDownload && File.Exists(isolatedMetadataPath)
    ? ReadObject(isolatedMetadataPath)
    : ReadObject(Path.Combine(template, "shortdrama-project.json"));
var referenceMetadata = ReadObject(Path.Combine(template, "shortdrama-project.json"));
var originalSource = referenceMetadata["sourceProjectDir"]?.ToString()
                     ?? throw new InvalidOperationException("模板元数据缺少 sourceProjectDir。");
var sourcePoster = Directory.EnumerateFiles(originalSource, "*.*")
    .FirstOrDefault(path => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
if (sourcePoster is null) throw new FileNotFoundException("模板源项目没有原始海报。");
if (!reuseDownload || !File.Exists(isolatedMetadataPath))
{
    File.Copy(sourcePoster, Path.Combine(source, Path.GetFileName(sourcePoster)), true);
    templateMetadata["sourceProjectDir"] = source;
    templateMetadata["workflowProjectDir"] = workflow;
    templateMetadata["workflowDirName"] = Path.GetFileName(workflow);
    await File.WriteAllTextAsync(isolatedMetadataPath, templateMetadata.ToJsonString(JsonDefaults.Options));
    await File.WriteAllTextAsync(Path.Combine(workflow, "shortdrama-project.json"), templateMetadata.ToJsonString(JsonDefaults.Options));
}

var title = templateMetadata["title"]?.ToString() ?? Path.GetFileName(originalSource);
var bookId = templateMetadata["bookId"]?.ToString();
var expectedEpisodes = templateMetadata["episodeCount"]?.GetValue<int>() ?? 0;
var platformSettings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
var mainSettings = ClientSettingsStore.Load();
var settings = HasDownloadConfiguration(platformSettings) ? platformSettings : mainSettings;
if (Present(mainSettings.HghighAccount, mainSettings.HghighPassword, mainSettings.HghighDeviceId))
{
    settings = mainSettings.Clone();
    settings.DramaSourceChain = "hghigh";
}
ShortDramaDramaServices.RefreshSettings(settings);
Log($"下载源：{settings.DramaSourceChain}；模板期望 {expectedEpisodes} 集");

DramaDownloadResult download;
var existing = Directory.EnumerateFiles(source, "*.mp4").Count();
if (reuseDownload && existing > 0)
    download = new DramaDownloadResult(true, source, existing, "复用隔离目录下载");
else
    download = await ShortDramaDramaServices.Downloader.DownloadAsync(
        new DramaDownloadRequest(source, source, title, bookId, "all", "1080P", 3, "source"),
        new InlineProgress<string>(Log), CancellationToken.None);
if (!download.Ok) throw new InvalidOperationException(download.Message ?? "下载失败。");
Log($"下载完成：{download.VideoCount} 集");

var runtimeConfig = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
var runtimePayload = ReadObject(runtimeConfig);
var referenceProjectTemplate = @"D:\code\weixin-channel-tool\assets\project_image\image_template_project_image_3";
if (File.Exists(Path.Combine(referenceProjectTemplate, "template.json")))
{
    runtimePayload["ProjectImageGenerationMode"] = "image_template";
    runtimePayload["ProjectImageTemplateId"] = "image-template-project-image-3";
    runtimePayload["ProjectImageTemplateName"] = "图片模板工程图3";
    runtimePayload["ProjectImageTemplateDir"] = referenceProjectTemplate;
    runtimePayload["ProjectImageCount"] = "4";
    await File.WriteAllTextAsync(runtimeConfig, runtimePayload.ToJsonString(JsonDefaults.Options));
}
var services = new ServiceCollection();
services.AddLogging();
services.AddShortDramaServices();
using var provider = services.BuildServiceProvider();
var work = provider.GetRequiredService<IWorkService>();
var progress = new InlineProgress<WorkRunEvent>(item => { if (!string.IsNullOrWhiteSpace(item.Message)) Log($"{item.DisplayName} | {item.Message}"); });
async Task RunStep(string key, string label)
{
    Log($"开始：{label}");
    var result = await work.RunProjectStepAsync(source, null, key, true, progress, CancellationToken.None, runtimeConfig);
    if (!result.Ok) throw new InvalidOperationException(result.Message ?? $"{label}失败。");
    Log($"完成：{label}");
}

if (!reuseDownload || !File.Exists(Path.Combine(workflow, "短剧信息.txt")))
{
    await RunStep("rewrite", "改写信息");
    workflow = ReadObject(isolatedMetadataPath)["workflowProjectDir"]?.ToString() ?? workflow;
    await RunStep("poster-rename", "生成海报");
}
else Log("复用已完成的改写信息和海报步骤");
Directory.CreateDirectory(Path.Combine(workflow, "videos"));
var info = ParseInfo(Path.Combine(workflow, "短剧信息.txt"));
var newTitle = info.GetValueOrDefault("新剧名", Path.GetFileName(workflow).TrimStart('_'));
var downloaded = Directory.EnumerateFiles(source, "*.mp4").OrderBy(EpisodeNumber).ToArray();
for (var index = 0; index < downloaded.Length; index++)
    File.Copy(downloaded[index], Path.Combine(workflow, "videos", $"{newTitle}-第{index + 1}集.mp4"), true);
Log($"整理剧集：{downloaded.Length} 集进入 workflow/videos");
var filled = await work.AutoFillProjectInfoAsync(source, null, CancellationToken.None);
Log($"完成：补齐字段（{string.Join('、', filled.UpdatedFields)}）");
Log("开始：生成工程图");
var existingProjectImages = Directory.EnumerateFiles(workflow, "工程图_*.png").Count();
if (!(reuseDownload && existingProjectImages == 4))
{
    var imageGenerator = provider.GetRequiredService<IProjectImageGenerator>();
    var projectImages = await imageGenerator.GenerateAsync(new ProjectImageGenerateRequest(
        workflow,
        Path.Combine(workflow, "videos"),
        workflow,
        referenceProjectTemplate,
        runtimeConfig,
        4,
        true,
        SourceVideos: Directory.EnumerateFiles(Path.Combine(workflow, "videos"), "*.mp4").OrderBy(EpisodeNumber).ToArray(),
        Progress: message => Log($"生成工程图 | {message}")), CancellationToken.None);
    if (projectImages.Count != 4)
        throw new InvalidOperationException($"生成工程图数量不正确：{projectImages.Count}/4");
}
else Log("复用本轮已生成的 4 张工程图");
Log("完成：生成工程图");

var scanner = provider.GetRequiredService<IProjectScanner>();
var dataService = new KuaishouPersonalProjectDataService(scanner);
var config = new KuaishouPersonalConfig
{
    Actors = "陆景深:男:赵峰;苏晚晴:女:母亲",
    Directors = "周叙白:男",
    Screenwriters = "顾承宇:男",
    AudienceGender = "男频",
    PlotLabels = "麻雀变凤凰;守护家人;成长奋斗",
    TagLabels = "逆袭;家庭;都市",
    ProductionOrganization = info.GetValueOrDefault("制作公司", string.Empty),
    FreeEpisodeCount = 2,
};
var data = await dataService.ResolveAsync(workflow, config, CancellationToken.None);
var preparation = new KuaishouPersonalPreparationService();
var prepared = await preparation.PrepareAsync(data, config, true, CancellationToken.None);
var issues = await preparation.ValidateAsync(workflow, CancellationToken.None);
foreach (var issue in issues) Log($"校验失败[{issue.Code}] {issue.Message}");

var reference = await SnapshotAsync(template);
var actual = await SnapshotAsync(workflow);
var differences = Compare(reference, actual);
foreach (var difference in differences) Log($"差异[{difference.Level}] {difference.Item}: {difference.Message}");
var sample = downloaded.FirstOrDefault() is { } path ? await ProbeAsync(path) : null;
var ok = issues.Count == 0 && differences.All(item => item.Level != "error") && download.VideoCount == expectedEpisodes;
var report = new
{
    generatedAt = DateTimeOffset.Now,
    uploadExecuted = false,
    templateDirectory = template,
    testRoot,
    download = new { download.Ok, download.VideoCount, download.Message, source = settings.DramaSourceChain, sample },
    steps = new[] { "download", "rewrite_info", "generate_poster", "generate_project_images", "auto_fill_info", "kuaishou_artifacts", "validate" },
    prepared,
    reference,
    actual,
    issues,
    differences,
    ok,
};
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonDefaults.Options));
Log($"测试完成：{(ok ? "通过" : "未通过")}；报告 {reportPath}");
try { File.Delete(runtimeConfig); } catch { }
return ok ? 0 : 1;

static async Task<Snapshot> SnapshotAsync(string directory)
{
    var info = ParseInfo(Path.Combine(directory, "短剧信息.txt"));
    var videos = Directory.Exists(Path.Combine(directory, "videos")) ? Directory.EnumerateFiles(Path.Combine(directory, "videos"), "*.mp4").ToArray() : [];
    var autoFill = ReadObject(Path.Combine(directory, "kuaishou-auto-fill.json"));
    var preview = ReadObject(Path.Combine(directory, "kuaishou-payload-preview.json"));
    return new Snapshot(info.GetValueOrDefault("原剧名", ""), info.GetValueOrDefault("新剧名", ""), ParseInt(info.GetValueOrDefault("集数")), videos.Length,
        await ImageSizeAsync(Path.Combine(directory, "快手横屏封面.jpg")), await ImageSizeAsync(Path.Combine(directory, "快手竖屏海报.jpg")),
        Directory.EnumerateFiles(directory, "工程图_*.png").Count(), autoFill.Select(pair => pair.Key).Order().ToArray(), preview.Select(pair => pair.Key).Order().ToArray(),
        preview["episodes"]?.AsArray().Count ?? 0);
}

static List<Difference> Compare(Snapshot expected, Snapshot actual)
{
    var result = new List<Difference>();
    void Equal<T>(string item, T left, T right) where T : IEquatable<T> { if (!left.Equals(right)) result.Add(new("error", item, $"模板={left}，测试={right}")); }
    Equal("original-title", expected.OriginalTitle, actual.OriginalTitle);
    Equal("info-episode-count", expected.InfoEpisodeCount, actual.InfoEpisodeCount);
    Equal("video-count", expected.VideoCount, actual.VideoCount);
    Equal("horizontal-size", expected.HorizontalSize, actual.HorizontalSize);
    Equal("vertical-size", expected.VerticalSize, actual.VerticalSize);
    Equal("project-images", expected.ProjectImages, actual.ProjectImages);
    Equal("payload-episodes", expected.PayloadEpisodes, actual.PayloadEpisodes);
    foreach (var key in expected.AutoFillKeys.Except(actual.AutoFillKeys)) result.Add(new("error", "auto-fill-schema", $"缺少字段 {key}"));
    foreach (var key in expected.PayloadKeys.Except(actual.PayloadKeys)) result.Add(new("warning", "payload-schema", $"测试预览未生成参考扩展字段 {key}"));
    return result;
}

static JsonObject ReadObject(string path) => JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidOperationException($"JSON 无效：{path}");
static Dictionary<string, string> ParseInfo(string path)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadLines(path)) { var index = line.IndexOfAny([':', '：']); if (index > 0) result[line[..index].Trim()] = line[(index + 1)..].Trim(); }
    return result;
}
static int EpisodeNumber(string path) { var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"第\s*(\d+)\s*集"); return match.Success ? int.Parse(match.Groups[1].Value) : int.MaxValue; }
static int ParseInt(string? value) { var match = Regex.Match(value ?? "", @"\d+"); return match.Success ? int.Parse(match.Value) : 0; }
static bool HasDownloadConfiguration(ClientSettings value) => (value.DramaSourceChain ?? "hgnew").Trim().ToLowerInvariant() switch
{
    "hgnew" => Present(value.HgnewAccount, value.HgnewPassword, value.HgnewUdid),
    "hghigh" => Present(value.HghighAccount, value.HghighPassword, value.HghighDeviceId),
    "mapleleaf" => Present(value.MapleleafAccount, value.MapleleafPassword, value.MapleleafUdid),
    _ => false,
};
static bool Present(params string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value));
static async Task<string> ImageSizeAsync(string path)
{
    if (!File.Exists(path)) return "missing";
    var probe = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var value in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", path }) probe.ArgumentList.Add(value);
    using var process = Process.Start(probe)!; var output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync(); return output.Trim();
}
static async Task<VideoProbe?> ProbeAsync(string path)
{
    var start = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var value in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,duration,bit_rate", "-of", "json", path }) start.ArgumentList.Add(value);
    using var process = Process.Start(start)!; var output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
    var stream = JsonNode.Parse(output)?["streams"]?.AsArray().FirstOrDefault()?.AsObject();
    return stream is null ? null : new(stream["width"]?.GetValue<int>() ?? 0, stream["height"]?.GetValue<int>() ?? 0, double.TryParse(stream["duration"]?.ToString(), out var duration) ? duration : 0, long.TryParse(stream["bit_rate"]?.ToString(), out var rate) ? rate : 0);
}

sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
sealed record Snapshot(string OriginalTitle, string NewTitle, int InfoEpisodeCount, int VideoCount, string HorizontalSize, string VerticalSize, int ProjectImages, string[] AutoFillKeys, string[] PayloadKeys, int PayloadEpisodes);
sealed record Difference(string Level, string Item, string Message);
sealed record VideoProbe(int Width, int Height, double DurationSeconds, long BitRate);
static class JsonDefaults { public static readonly JsonSerializerOptions Options = new() { WriteIndented = true }; }
