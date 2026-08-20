using System.Text.Json;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

var derivedOnly = args.Length == 2 && args[0] == "--derived-only";
var projectArgument = derivedOnly ? args[1] : args.FirstOrDefault();
if (projectArgument is null || !Directory.Exists(projectArgument))
{
    Console.Error.WriteLine("用法：TikTokReferenceSourcePackageSpike [--derived-only] <workflow项目目录>");
    return 2;
}

var projectDir = Path.GetFullPath(projectArgument);
var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
using var metadata = File.Exists(metadataPath)
    ? JsonDocument.Parse(File.ReadAllText(metadataPath))
    : JsonDocument.Parse("{}");
var root = metadata.RootElement;
string ReadString(string name) =>
    root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : "";
int ReadInt(string name) =>
    root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

var title = FirstNonEmpty(
    ReadString("displayName"),
    ReadString("title"),
    Path.GetFileName(projectDir).TrimStart('_'));
var item = new QueueProjectItem
{
    ProjectDir = projectDir,
    DisplayName = title,
    OriginalTitle = FirstNonEmpty(ReadString("originalTitle"), ReadString("sourceName"), title),
    NewTitle = Path.GetFileName(projectDir).TrimStart('_'),
    EpisodeCount = ReadInt("episodeCount"),
    GenreCategory = ReadString("category"),
    Description = ReadString("intro"),
};
var settings = ClientSettingsStore.Load();
Console.WriteLine($"项目：{item.NewTitle}");
Console.WriteLine($"图片模型：{settings.ImageProvider} / " +
                  (PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider) == "ofox_image2"
                      ? settings.OfoxImage2ModelId
                      : settings.ImageModelId));

var logger = new Action<string>(message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"));
var package = TikTokReferenceSourcePackageService.GetRoot(projectDir);
if (derivedOnly)
    await TikTokReferenceSourcePackageService.RefreshDerivedImagesAsync(projectDir, logger, CancellationToken.None);
else
    package = await TikTokReferenceSourcePackageService.GenerateAsync(
        item,
        settings,
        forceRerun: true,
        logger,
        CancellationToken.None);
var screenshots = TikTokSourceFileInfoScreenshotService.Generate(
    projectDir,
    item.NewTitle,
    log: logger);

Console.WriteLine($"素材包：{package}");
foreach (var screenshot in screenshots)
    Console.WriteLine($"截图：{screenshot}");
return 0;

static string FirstNonEmpty(params string[] values) =>
    values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
