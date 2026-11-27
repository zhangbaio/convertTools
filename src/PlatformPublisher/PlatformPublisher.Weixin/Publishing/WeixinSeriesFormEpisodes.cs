using System.Text.Json.Nodes;
using PlatformPublisher.Common.Models;
using static PlatformPublisher.Weixin.Publishing.WeixinSeriesFormService;

namespace PlatformPublisher.Weixin.Publishing;

internal sealed record WeixinEpisodeFile(int Number, string Path, JsonObject? QueueItem);

internal static class WeixinSeriesFormEpisodes
{
    internal static IReadOnlyList<WeixinEpisodeFile> Read(JsonObject second, string sourceDirectory, List<string> errors)
    {
        var entries = new List<(string Path, JsonObject? QueueItem)>();
        if (second["upload_queue"]?["items"] is JsonArray queue)
        {
            foreach (var node in queue)
            {
                if (node is JsonObject item)
                {
                    if (item["enabled"]?.GetValue<bool>() != false)
                        entries.Add((Text(item["path"]), item));
                }
                else
                    entries.Add((Text(node), null));
            }
        }
        else if (second["upload"]?["paths"] is JsonArray paths)
            entries.AddRange(paths.Select(path => (Text(path), (JsonObject?)null)));

        var files = new List<WeixinEpisodeFile>();
        foreach (var (path, item) in entries)
        {
            var resolved = ReadPaths(path, sourceDirectory, "分集视频", errors).FirstOrDefault();
            if (resolved is null) continue;
            try { files.Add(new(WeixinSeriesApiProject.ParseEpisode(resolved), resolved, item)); }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        }
        foreach (var duplicate in files.GroupBy(file => file.Number).Where(group => group.Count() > 1))
            errors.Add($"第 {duplicate.Key} 集有多个视频文件。");
        return files.OrderBy(file => file.Number).ToArray();
    }

    internal static IReadOnlyList<WeixinEpisodeFile> Select(IReadOnlyList<WeixinEpisodeFile> files, string mode,
        string from, string to, string indexes, WeixinPublishOptions options, int requestedCount, List<string> errors)
    {
        var first = 1;
        var last = int.MaxValue;
        if (options.OverrideSeriesEpisodeSelection)
        {
            mode = options.EpisodeSelectionMode switch { "explicit" => "指定集数", "range" => "连续区间", _ => "全部" };
            first = options.StartEpisodeIndex;
            last = (int)Math.Min(int.MaxValue, (long)first + Math.Max(1, requestedCount) - 1);
            indexes = options.EpisodeIndexes;
        }
        else if (mode == "连续区间" &&
                 (!int.TryParse(from, out first) || first < 1 || !int.TryParse(to, out last) || last < first))
        {
            errors.Add("请填写有效的起始、结束集数。");
            return [];
        }

        IEnumerable<WeixinEpisodeFile> selected = files;
        if (mode == "指定集数")
        {
            var wanted = new SortedSet<int>();
            var tokens = indexes.Split([',', '，', ';', '；', '、', ' ', '\n'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                var bounds = token.Split('-', StringSplitOptions.TrimEntries);
                var end = 0;
                if (bounds.Length is < 1 or > 2 || !int.TryParse(bounds[0], out var start) || start is < 1 or > 9999 ||
                    (bounds.Length == 2 && (!int.TryParse(bounds[1], out end) || end < start || end > 9999)))
                {
                    errors.Add($"无效的指定集数「{token}」。");
                    continue;
                }
                for (var index = start; index <= (bounds.Length == 2 ? end : start); index++)
                    wanted.Add(index);
            }
            var missing = wanted.Except(files.Select(item => item.Number)).ToArray();
            if (missing.Length > 0)
                errors.Add("缺少指定分集：" + string.Join("、", missing.Take(12)) + (missing.Length > 12 ? " 等" : "") + "。");
            selected = files.Where(file => wanted.Contains(file.Number));
        }
        else if (mode == "连续区间")
        {
            selected = files.Where(file => file.Number >= first && file.Number <= last);
            if (!options.OverrideSeriesEpisodeSelection && (long)last - first + 1 != selected.Select(file => file.Number).Distinct().Count())
                errors.Add($"所选第 {first}–{last} 集区间中有缺集。");
        }
        var result = selected.ToArray();
        if (result.Length == 0) errors.Add("没有可上传的分集视频。");
        return result;
    }

    internal static bool TryCalculateFreeCount(double seconds, int total, IReadOnlyList<WeixinEpisodeFile> episodes,
        IReadOnlyList<double?> durations, out int free, out double cumulative)
    {
        free = 0;
        cumulative = 0;
        if (total < 3) return false;
        for (var i = 0; i < Math.Min(10, total); i++)
        {
            if (i >= episodes.Count || episodes[i].Number != i + 1 || durations[i] is not > 0)
                return false;
            cumulative += durations[i]!.Value;
            free++;
            if (free >= 3 && cumulative >= seconds) break;
        }
        return true;
    }

    internal static void Apply(JsonObject second, IReadOnlyList<WeixinEpisodeFile> selected)
    {
        if (second["upload"] is not JsonObject upload)
        {
            upload = new JsonObject();
            second["upload"] = upload;
        }
        upload["paths"] = new JsonArray(selected.Select(file => (JsonNode?)JsonValue.Create(file.Path)).ToArray());
        if (second["upload_queue"] is JsonObject queue)
        {
            queue["items"] = new JsonArray(selected.Select(file =>
            {
                var item = file.QueueItem?.DeepClone().AsObject() ?? new JsonObject();
                item["path"] = file.Path;
                item["enabled"] = true;
                return (JsonNode?)item;
            }).ToArray());
        }
    }
}
