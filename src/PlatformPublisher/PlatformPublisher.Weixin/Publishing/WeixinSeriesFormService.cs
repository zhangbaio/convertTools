using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure;
using static PlatformPublisher.Weixin.Publishing.WeixinSeriesFields;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinSeriesFormValue(string Key, string Label, string Value, string Source);
public sealed record WeixinSeriesFormEpisode(int Number, string Path, double? Seconds, bool Selected);
public sealed record WeixinSeriesFormPreview(
    IReadOnlyList<WeixinSeriesFormValue> Fields, IReadOnlyList<WeixinSeriesFormEpisode> Episodes,
    IReadOnlyList<string> Errors, string TrialSummary);

public sealed class WeixinSeriesFormService(
    WeixinSeriesPresetStore store,
    IExternalProcessRunner processRunner,
    IProjectInfoParser projectInfoParser,
    IAiRuntimeSettingsProvider aiSettings,
    string? dataRoot = null)
{
    internal const string ResolvedFormKey = "_resolved_series_form";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<WeixinSeriesFormPreview> PreviewAsync(PublishJob job, WeixinSeriesProjectForm? form,
        CancellationToken cancellationToken) =>
        (await BuildAsync(job, store.LoadAccountForm(job.AccountId), form, preview: true, cancellationToken)).Preview;

    internal async Task<string?> PrepareUploadAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.AccountId))
            return null;

        var form = store.LoadProject(job.AccountId, job.ProjectDirectory);
        if (!string.IsNullOrEmpty(form?.PresetId))
            throw new InvalidOperationException("旧项目配置缺少关联数据，请打开项目表单检查并重新保存。");
        var accountValues = store.LoadAccountForm(job.AccountId);
        if (accountValues.Count == 0 && (form is null || (form.Values.Count == 0 && string.IsNullOrEmpty(form.ConfigPath))))
            return null;

        progress?.Report("正在合并账号发布配置和项目表单");
        var (root, preview, reportInputs) = await BuildAsync(job, accountValues, form, preview: false, cancellationToken);
        if (preview.Errors.Count != 0)
            throw new InvalidOperationException("剧集上传表单校验失败：" + string.Join("；", preview.Errors.Take(6)));

        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{job.AccountId}\n{job.Id}")));
        var folder = Path.Combine(dataRoot ?? PlatformPublisherPaths.DataRoot, "weixin", "series-forms", identity);
        Directory.CreateDirectory(folder);
        if (reportInputs is not null)
        {
            var reportPath = await WeixinSeriesFormReport.RenderAsync(projectInfoParser, reportInputs, folder, cancellationToken);
            PutAction((JsonArray)root["first_page"]!["actions"]!, "upload", Qualification, [reportPath]);
        }
        var path = Path.Combine(folder, "series-form.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, root.ToJsonString(JsonOptions), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
        return path;
    }

    private async Task<(JsonObject Root, WeixinSeriesFormPreview Preview, WeixinSeriesFormReportInputs? Report)>
        BuildAsync(PublishJob job, IReadOnlyDictionary<string, string> accountValues, WeixinSeriesProjectForm? projectForm,
            bool preview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(job.ProjectDirectory))
            throw new DirectoryNotFoundException("请选择剧集项目目录。");
        var explicitConfigPath = string.IsNullOrWhiteSpace(projectForm?.ConfigPath) ? job.ConfigPath : projectForm.ConfigPath;
        if (!string.IsNullOrWhiteSpace(explicitConfigPath) && !File.Exists(explicitConfigPath))
            throw new FileNotFoundException("找不到所选剧集配置文件。", explicitConfigPath);
        var path = (string.IsNullOrWhiteSpace(explicitConfigPath)
            ? WeixinSeriesConfigOverrideService.ResolveSourceConfigPath(job) : Path.GetFullPath(explicitConfigPath)) ??
            throw new InvalidOperationException("找不到剧集配置。请选择项目 JSON，或先在任务队列执行「素材校验」。");
        await using var stream = File.OpenRead(path);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject ??
            throw new InvalidOperationException("剧集配置不是有效的 JSON 对象。");
        if (Text(root["task_type"]) is { Length: > 0 } taskType &&
            !taskType.Equals("series_submission", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择视频号剧集上传配置。");
        var sourceDirectory = Path.GetDirectoryName(path)!;
        WeixinSeriesConfigOverrideService.AbsolutizeKnownPaths(root, sourceDirectory);
        var first = root["first_page"] as JsonObject ??
            throw new InvalidOperationException("剧集配置缺少第一页表单。");
        var actions = first["actions"] as JsonArray ??
            throw new InvalidOperationException("剧集配置缺少第一页字段。");
        var second = root["second_page"] as JsonObject ??
            throw new InvalidOperationException("剧集配置缺少分集上传表单。");
        var errors = new List<string>();
        var fields = All.ToDictionary(field => field.Key, field => ReadValue(field.Key, actions, first));
        foreach (var (key, value) in accountValues)
            Set(key, value, "账号配置");
        foreach (var (key, value) in projectForm?.Values ?? [])
            Set(key, value, "本项目覆盖");

        string Value(string key) => fields[key].Value;
        void Set(string key, string? value, string source)
        {
            if (fields.TryGetValue(key, out var field) && !string.IsNullOrWhiteSpace(value))
                fields[key] = field with { Value = value.Trim(), Source = source };
        }

        var classify = Value(DramaType) == "自动";
        if (classify)
        {
            var detected = await ReadProjectDramaTypeAsync(sourceDirectory, job.ProjectDirectory, cancellationToken);
            if (detected is not null)
            {
                Set(DramaType, detected, "项目类型标记");
                classify = false;
            }
        }

        var generateIntro = Value(GenerateText) == "启用" && fields[Intro].Source == "项目";
        var generateRecommendation = Value(GenerateText) == "启用" && fields[Recommendation].Source == "项目";
        if (classify || generateIntro || generateRecommendation)
        {
            try
            {
                var generated = await WeixinSeriesFormAgent.GenerateAsync(aiSettings.Load(), Value(Title),
                    Value(Intro), classify, generateIntro, generateRecommendation, cancellationToken);
                if (classify) Set(DramaType, generated.DramaType, "剧集类型 Agent");
                if (generateIntro) Set(Intro, generated.Intro, "发布信息 Agent");
                if (generateRecommendation) Set(Recommendation, generated.Recommendation, "发布信息 Agent");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TimeoutException)
            {
                errors.Add(ex is JsonException ? "AI Agent 返回的结果无法解析。" : ex.Message);
            }
        }
        if (Value(DramaType) == "自动")
            errors.Add("未能确定剧集类型，请为本项目指定类型。");

        var episodes = WeixinSeriesFormEpisodes.Read(second, sourceDirectory, errors);
        var uploadOptions = WeixinPublishOptions.FromJob(job);
        var selected = WeixinSeriesFormEpisodes.Select(episodes, Value(EpisodeMode), Value(EpisodeStart),
            Value(EpisodeEnd), Value(EpisodeIndexes), uploadOptions, job.PublishCount, errors);
        var total = int.TryParse(Value(Total), out var count) ? count : 0;
        if (total <= 0)
            errors.Add("请填写有效的总集数。");
        if (Value(EpisodeMode) == "全部" && !uploadOptions.OverrideSeriesEpisodeSelection && total > 0 &&
            (selected.Count != total || !selected.Select(item => item.Number).SequenceEqual(Enumerable.Range(1, selected.Count))))
            errors.Add("「全部」模式要求分集从第 1 集连续至总集数，请检查缺集或多余分集。");
        var seconds = 0d;
        if (Value(TrialSeconds).Length != 0 &&
            (!double.TryParse(Value(TrialSeconds), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
             !double.IsFinite(seconds) || seconds < 0))
        {
            errors.Add("试看秒数须为大于或等于 0 的数字。");
            seconds = 0;
        }
        var measures = await ProbeEpisodesAsync(episodes, preview ? episodes.Count : seconds > 0 ? Math.Min(10, total) : 0,
            cancellationToken);
        var trialSummary = string.Empty;
        if (seconds > 0)
        {
            if (!WeixinSeriesFormEpisodes.TryCalculateFreeCount(seconds, total, episodes, measures, out var free, out var cumulative))
                errors.Add("按秒换算需要至少 3 集，并且从第 1 集起有连续、可读取的分集时长。");
            else
            {
                Set(FreeEpisodes, free.ToString(CultureInfo.InvariantCulture), "按试看秒数换算");
                trialSummary = $"设定 {seconds:0.##} 秒 · 免费 {free} 集 · 实际累计 {cumulative:0.##} 秒";
            }
        }
        if (!int.TryParse(Value(FreeEpisodes), out var actualFree) || actualFree is < 3 or > 10 || actualFree > total)
            errors.Add("免费集数须为 3–10 集且不超过总集数。");

        foreach (var key in new[] { Title, Intro, Producer })
            if (Value(key).Length == 0) errors.Add($"缺少「{fields[key].Label}」。");
        if (uploadOptions.SeriesUploadMode == WeixinSeriesUploadMode.ApiSubmit)
        {
            if (Value(Intro).Length > 100 || Value(Recommendation).Length > 15)
                errors.Add("接口提审的简介最多 100 字，推荐语最多 15 字。");
            if (total > 0 && (selected.Count != total || !selected.Select(item => item.Number).SequenceEqual(Enumerable.Range(1, selected.Count))))
                errors.Add("接口提审须上传从第 1 集起连续且与总集数一致的视频。");
        }

        var qualified = Value(Qualification) is "重点/普通微短剧" or "重点微短剧" or "普通微短剧";
        var report = WeixinSeriesFormReport.Resolve(Value, qualified, job.ProjectDirectory, sourceDirectory, errors);
        var paths = new Dictionary<string, string[]>();
        foreach (var field in All.Where(field => field.Kind is WeixinSeriesFieldKind.Attachment or WeixinSeriesFieldKind.Attachments))
        {
            if (field.Key is Seal or Signature || (field.Key == Report && (report is not null || qualified)) ||
                (field.Key == QualificationFile && !qualified) ||
                (field.Key == AiProduction && Value(AiDeclared) == "否") ||
                (field.Key == ActorPhotos && Value(DramaType) != "真人") ||
                (field.Key == AiOwnership && Value(AiOwnershipMode) == "关闭"))
                continue;
            paths[field.Key] = ReadPaths(Value(field.Key), sourceDirectory, field.Label, errors);
        }

        void RequireFiles(string key)
        {
            if (!paths.TryGetValue(key, out var pathsForField) || pathsForField.Length == 0)
                errors.Add($"缺少「{fields[key].Label}」。");
        }
        RequireFiles(Poster);
        RequireFiles(Production);
        if (qualified)
        {
            if (Value(Permit).Length == 0) errors.Add("缺少备案号或发行许可证号。");
            RequireFiles(QualificationFile);
        }
        else
        {
            if (!decimal.TryParse(Value(Cost), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) || cost <= 0)
                errors.Add("请填写有效的制作成本（万元）。");
            if (report is null) RequireFiles(Report);
        }
        if (Value(Identity) is "剧目发行方" or "版权方/授权播出方") RequireFiles(Authorization);
        if (Value(AiDeclared) == "是") RequireFiles(AiProduction);
        if (Value(AiOwnershipMode) == "启用") RequireFiles(AiOwnership);
        if (paths.GetValueOrDefault(AiOwnership)?.Length > 3)
            errors.Add("最多可选 3 个 AI 软件权属证明文件。");
        if (Value(AiOwnershipMode) is "启用" or "关闭")
            first["include_ai_software_ownership_proof"] = Value(AiOwnershipMode) == "启用";

        if (Value(DramaType) == "真人")
        {
            var actors = ParseActors(Value(Actors), errors);
            if (actors.Count < 2 || paths.GetValueOrDefault(ActorPhotos)?.Length != actors.Count)
                errors.Add("真人剧需要至少两位演员的姓名、角色、简介，照片数量须与演员人数一致。");
            first["actors"] = actors;
            if (uploadOptions.SeriesUploadMode == WeixinSeriesUploadMode.Browser &&
                (fields[Actors].Source != "项目" || fields[ActorPhotos].Source != "项目" ||
                 !actions.OfType<JsonObject>().Any(action => Text(action["type"]) == "fill" && Label(action).Contains("演员", StringComparison.Ordinal))))
                errors.Add("该剧的演员资料请使用接口上传。浏览器方式需要项目 JSON 中已有可执行的演员表单动作。");
        }

        foreach (var key in new[] { Title, Intro, Recommendation, Total, FreeEpisodes, Producer, Cost, Permit })
            if (fields[key].Source != "项目")
                PutAction(actions, "fill", key, [Value(key)]);
        foreach (var key in new[] { DramaType, Monetization, Qualification, Identity })
            if (fields[key].Source != "项目")
                PutAction(actions, "choose", key, [Value(key)]);
        if (fields[AiDeclared].Source != "项目")
            PutAction(actions, "set_checked", AiDeclared, [Value(AiDeclared)]);
        foreach (var (key, filePaths) in paths)
        {
            if (filePaths.Length > 0 && (fields[key].Source != "项目" || key == Report))
                PutAction(actions, "upload", key == Report ? Qualification : key, filePaths);
        }
        if (Value(AiOwnershipMode) == "关闭")
            RemoveUploads(actions, AiOwnership);
        if (Value(AiDeclared) == "否")
            RemoveUploads(actions, AiProduction);
        if (Value(DramaType) != "真人")
            RemoveUploads(actions, ActorPhotos);
        if (qualified)
            RemoveUploads(actions, Qualification);
        else
            RemoveUploads(actions, QualificationFile);
        var unusedLabels = LabelsFor(qualified ? Cost : Permit);
        foreach (var action in actions.OfType<JsonObject>()
                     .Where(action => Text(action["type"]) == "fill" && unusedLabels.Contains(Label(action))).ToArray())
            actions.Remove(action);
        if (report is not null)
            fields[Report] = fields[Report] with { Value = "执行时生成成本报表.png", Source = "自动生成" };
        WeixinSeriesFormEpisodes.Apply(second, selected);
        root[ResolvedFormKey] = true;
        var selectedNumbers = selected.Select(item => item.Number).ToHashSet();
        return (root, new WeixinSeriesFormPreview(fields.Values.ToArray(),
            episodes.Select((item, index) => new WeixinSeriesFormEpisode(item.Number, item.Path,
                measures[index], selectedNumbers.Contains(item.Number))).ToArray(), errors.Distinct().ToArray(), trialSummary), report);
    }

    private async Task<double?[]> ProbeEpisodesAsync(IReadOnlyList<WeixinEpisodeFile> episodes, int count, CancellationToken ct)
    {
        var measures = new double?[episodes.Count];
        if (count == 0) return measures;
        var ffprobe = BundledToolResolver.TryResolveBinary("ffprobe") ?? "ffprobe";
        await Parallel.ForEachAsync(Enumerable.Range(0, Math.Min(count, episodes.Count)),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
            {
                try
                {
                    var result = await processRunner.RunAsync(ffprobe,
                        ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", episodes[index].Path], null, token);
                    if (result.ExitCode == 0 && double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0)
                        measures[index] = value;
                }
                catch (System.ComponentModel.Win32Exception) { }
            });
        return measures;
    }

    private static async Task<string?> ReadProjectDramaTypeAsync(string sourceDirectory, string projectDirectory, CancellationToken ct)
    {
        var detected = new HashSet<string>();
        foreach (var directory in new[] { sourceDirectory, projectDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var name in new[] { "简介.txt", "短剧信息.txt" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            var text = await File.ReadAllTextAsync(path, ct);
            foreach (Match match in Regex.Matches(text, @"(?m)^\s*(?:剧集类型|剧目类型|短剧类型|类型)\s*[：:]\s*(数字真人|真人剧?|动漫|漫剧)(?=\s|[，,。；;、]|$)"))
                detected.Add(match.Groups[1].Value switch { "真人剧" => "真人", "动漫" => "漫剧", var value => value });
        }
        return detected.Count == 1 ? detected.Single() : null;
    }

    private static WeixinSeriesFormValue ReadValue(string key, JsonArray actions, JsonObject first)
    {
        var labels = LabelsFor(key);
        var field = All.First(item => item.Key == key);
        var actionRecords = actions.OfType<JsonObject>().Where(action => labels.Contains(Label(action))).ToArray();
        string value;
        if (field.Kind is WeixinSeriesFieldKind.Attachment or WeixinSeriesFieldKind.Attachments)
            value = string.Join(Environment.NewLine, actionRecords.Where(action => Text(action["type"]) == "upload")
                .SelectMany(action => (action["paths"] as JsonArray ?? []).Select(Text)).Where(value => value.Length != 0));
        else if (key == Actors)
            value = string.Join(Environment.NewLine, (first["actors"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(actor => $"{Text(actor["name"])} | {Text(actor["role"])} | {Text(actor["profile"])}"));
        else
        {
            var action = actionRecords.LastOrDefault(item => Text(item["type"]) is "choose" or "fill" or "set_checked");
            value = Text(action?["type"]) switch
            {
                "choose" => Text(action?["option_text"]),
                "set_checked" => action?["enabled"]?.GetValue<bool>() == false ? "否" : "是",
                _ => Text(action?["value"]),
            };
        }
        return new WeixinSeriesFormValue(key, field.Label, value, "项目");
    }

    private static string[] LabelsFor(string field) => field switch
    {
        Report => [Qualification, Report, "成本配置比例情况报告"],
        Permit => [Permit, "发行许可证号", "网络剧片发行许可证号"],
        AiOwnership => [AiOwnership, "AI 软件权属证明"],
        _ => [field],
    };

    private static string Label(JsonObject action)
    {
        var label = new[] { Text(action["label"]), Text(action["field_label"]), Text(action["text"]) }
            .FirstOrDefault(value => value.Length != 0);
        if (label is not null) return label;
        var selector = Text(action["selector"]);
        if (Text(action["type"]) == "upload" && selector.Contains("成本配置比例情况报告", StringComparison.Ordinal)) return Report;
        if (selector.Contains("制作成本", StringComparison.Ordinal) || selector.Contains("成本", StringComparison.Ordinal)) return Cost;
        if (selector.Contains("备案号", StringComparison.Ordinal) || selector.Contains("发行许可证号", StringComparison.Ordinal)) return Permit;
        return string.Empty;
    }

    private static void PutAction(JsonArray actions, string type, string key, string[] values)
    {
        var labels = LabelsFor(key == Qualification && type == "upload" ? Report : key);
        var existing = actions.OfType<JsonObject>().Where(item => Text(item["type"]) == type && labels.Contains(Label(item))).ToArray();
        var action = existing.FirstOrDefault();
        if (action is null)
        {
            action = new JsonObject { ["type"] = type, [type == "choose" ? "field_label" : "label"] = key };
            if (type == "upload") action["text"] = key is Poster or Promotion ? "选择图片" : "选择文件";
            if (key == Intro) action["control"] = "textarea";
            var agreement = actions.OfType<JsonObject>().FirstOrDefault(item => Label(item) == "我已知悉并同意");
            if (agreement is null) actions.Add(action);
            else actions.Insert(actions.IndexOf(agreement), action);
        }
        foreach (var duplicates in existing.Skip(1)) actions.Remove(duplicates);
        switch (type)
        {
            case "upload":
                if (key == Qualification) action["label"] = Qualification;
                action["paths"] = new JsonArray(values.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
                break;
            case "choose": action["option_text"] = values[0]; break;
            case "set_checked": action["enabled"] = values[0] == "是"; break;
            default: action["value"] = values[0]; break;
        }
    }

    private static void RemoveUploads(JsonArray actions, string key)
    {
        var labels = LabelsFor(key == Qualification ? Report : key);
        foreach (var action in actions.OfType<JsonObject>().Where(a => Text(a["type"]) == "upload" && labels.Contains(Label(a))).ToArray())
            actions.Remove(action);
    }

    internal static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text)
        ? text.Trim() : node is JsonValue ? node.ToJsonString() : string.Empty;

    internal static string[] ReadPaths(string value, string directory, string label, List<string> errors)
    {
        var paths = new List<string>();
        foreach (var entry in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(entry.Trim('"')), directory);
                if (File.Exists(path) && new FileInfo(path).Length > 0) paths.Add(path);
                else errors.Add($"「{label}」文件缺失或为空：{Path.GetFileName(path)}。");
            }
            catch (ArgumentException) { errors.Add($"「{label}」文件路径无效。"); }
        }
        return paths.ToArray();
    }

    private static JsonArray ParseActors(string text, List<string> errors)
    {
        var actors = new JsonArray();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|', 3, StringSplitOptions.TrimEntries);
            if (fields.Length != 3 || fields.Any(string.IsNullOrWhiteSpace))
                errors.Add("演员资料每行须填写「姓名 | 角色 | 简介」。");
            else actors.Add(new JsonObject { ["name"] = fields[0], ["role"] = fields[1], ["profile"] = fields[2] });
        }
        return actors;
    }
}
