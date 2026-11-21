using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Weixin.Publishing;

internal sealed record WeixinSeriesApiFile(string Label, string Path, int? Episode, long Length, long ModifiedUtcTicks)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool Preview => Label is "AI制作证明" or "剧目制作证明材料" or "版权采买&播出授权证明材料";
    public string Kind => Episode.HasValue ? "video" : Preview ? "preview" : "bizmedia";
}

internal sealed record WeixinSeriesApiReceipt(WeixinSeriesApiFile File, string MediaId);

internal sealed record WeixinSeriesApiProject(
    string Title, string UserAgent, IReadOnlyList<WeixinSeriesApiFile> Files, WeixinSeriesApiForm? Form)
{
    public int EpisodeCount => Files.Count(file => file.Episode.HasValue);

    public static async Task<WeixinSeriesApiProject> LoadAsync(string configPath, string projectName,
        WeixinPublishOptions options, int requestedCount, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false));
        var root = document.RootElement;
        var taskType = root.Text("task_type");
        if (taskType.Length > 0 && !taskType.Equals("series_submission", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择视频号剧集配置。");

        var first = root.Get("first_page");
        var actions = first.Get("actions").Items().ToArray();
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var materials = actions.Where(action => action.Text("type") == "upload")
            .SelectMany(action => action.Get("paths").Items()
                .Select(item => (Label: Label(action), Path: ResolvePath(item.AsText(), baseDir), Episode: (int?)null)));
        var second = root.Get("second_page");
        if (options.SeriesUploadMode == WeixinSeriesUploadMode.ApiSubmit &&
            second.Get("enter_submit_page").IsFalse("enabled"))
            throw new InvalidOperationException("项目配置要求在提审前停止。请选择「仅上传」或修改项目提审设置。");
        var queue = second.Get("upload_queue").Get("items");
        var episodes = (queue.ValueKind == JsonValueKind.Array
                ? queue.Items().Where(item => !item.IsFalse("enabled"))
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.AsText() : item.Text("path"))
                : second.Get("upload").Get("paths").Items().Select(item => item.AsText()))
            .Select(path => ResolvePath(path, baseDir))
            .Select(path => (Label: "剧集视频", Path: path, Episode: (int?)ParseEpisode(path)))
            .OrderBy(item => item.Episode).ToArray();
        if (episodes.Select(item => item.Episode).Distinct().Count() != episodes.Length)
            throw new InvalidOperationException("剧集视频的集号有重复。");
        if (options.EpisodeSelectionMode == "range")
            episodes = episodes.Where(item => item.Episode >= options.StartEpisodeIndex)
                .Take(Math.Max(1, requestedCount)).ToArray();
        else if (options.EpisodeSelectionMode == "explicit")
        {
            var numbers = options.EpisodeIndexes.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => ParseNumber(text, "指定集数", int.MaxValue)).ToHashSet();
            var missing = numbers.Except(episodes.Select(item => item.Episode!.Value)).Order().ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"配置中没有可上传的第 {string.Join("、", missing)} 集。");
            episodes = episodes.Where(item => numbers.Contains(item.Episode!.Value)).ToArray();
        }
        if (episodes.Length == 0)
            throw new InvalidOperationException("没有选中的剧集视频。");

        var files = materials.Concat(episodes).Select(item => GetFile(item.Label, item.Path, item.Episode)).ToArray();
        var form = options.SeriesUploadMode == WeixinSeriesUploadMode.ApiSubmit
            ? WeixinSeriesApiForm.Create(first, actions, files)
            : null;
        var title = form?.Title ?? actions.LastOrDefault(action => action.Text("type") == "fill" && Label(action) == "剧目名称").Text("value");
        return new(title.Length == 0 ? projectName : title, root.Get("browser").Text("user_agent"), files, form);
    }

    internal static string Label(JsonElement action) =>
        new[] { action.Text("label"), action.Text("field_label"), action.Text("text") }
            .FirstOrDefault(text => text.Length > 0) ?? string.Empty;

    internal static int ParseNumber(string value, string label, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 && number <= maximum
            ? number
            : throw new InvalidOperationException($"「{label}」须为 1–{maximum} 的整数。");

    private static string ResolvePath(string value, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("剧集配置包含空文件路径。");
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value), baseDir);
    }

    private static int ParseEpisode(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var pattern in new[] { @"第\s*([0-9]+)\s*集", @"(?i)EP\s*([0-9]+)", @"^([0-9]+)$" })
        {
            var match = Regex.Match(stem, pattern);
            if (match.Success)
                return ParseNumber(match.Groups[1].Value, $"{stem} 集号", int.MaxValue);
        }

        throw new InvalidOperationException($"无法从文件名确认集号：{Path.GetFileName(path)}，请使用「第N集」或「EPN」命名。");
    }

    private static WeixinSeriesApiFile GetFile(string label, string path, int? episode)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
            throw new InvalidOperationException($"上传文件缺失或为空：{(label.Length == 0 ? "剧集材料" : label)} / {file.Name}");
        return new(label, path, episode, file.Length, file.LastWriteTimeUtc.Ticks);
    }
}

internal static class WeixinSeriesApiJson
{
    public static JsonElement Get(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;

    public static string Text(this JsonElement value, string name) => value.Get(name).AsText();

    public static string AsText(this JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        _ => string.Empty,
    };

    public static bool IsFalse(this JsonElement value, string name) => value.Get(name).ValueKind == JsonValueKind.False;

    public static IEnumerable<JsonElement> Items(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
