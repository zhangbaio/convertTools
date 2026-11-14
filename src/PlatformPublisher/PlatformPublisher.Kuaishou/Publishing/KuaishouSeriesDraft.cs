using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouSeriesDraftInput
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required bool Enterprise { get; init; }
    public required bool Creating { get; init; }
    public string MiniSeriesId { get; init; } = string.Empty;
    public required int SeriesContentType { get; init; }
    public int? ProductMethod { get; init; }
    public required int AuthorDeclaration { get; init; }
    public required int CopyrightProofType { get; init; }
    public required int SaleType { get; init; }
    public required int PriceCents { get; init; }
    public required int EpisodePriceCents { get; init; }
    public required bool UpdateEnd { get; init; }
    public required bool SmallAmountUnlock { get; init; }
    public int? UnlockCount { get; init; }
    public required int FreeEpisodeCount { get; init; }
    public required IReadOnlyList<int> TagIds { get; init; }
    public IReadOnlyList<int> PlotIds { get; init; } = [];
    public int? AudienceGender { get; init; }
    public required string CoverImgKey { get; init; }
    public required string EpisodeCoverImgKey { get; init; }
    public required IReadOnlyList<string> EpisodeTitles { get; init; }
    public IReadOnlyList<string> EpisodeIds { get; init; } = [];
    public required string ProductionForm { get; init; }
    public required double ProductionCost { get; init; }
    public required double EpisodeAvgTime { get; init; }
    public required long ProductionDate { get; init; }
    public required long DisplayDate { get; init; }
    public required string BroadcastPlatform { get; init; }
    public required string BroadcastChannel { get; init; }
    public required string ProductionOrg { get; init; }
    public required string CreditName { get; init; }
    public required string CreditGender { get; init; }
    public required string DirectorName { get; init; }
    public required string DirectorGender { get; init; }
    public required string ScreenwriterName { get; init; }
    public required string ScreenwriterGender { get; init; }
    public required string ProducerName { get; init; }
    public required string ProducerGender { get; init; }
    public required IReadOnlyList<KuaishouPersonalActor> Actors { get; init; }
    public required IReadOnlyList<string> ProjectImageKeys { get; init; }
    public required string CommitmentKey { get; init; }
}

public sealed record KuaishouSeriesFieldLog(
    string ContentTypeLabel,
    string ProductMethodLabel,
    string AuthorDeclarationLabel,
    string CopyrightProofTypeLabel,
    string SaleTypeLabel,
    string SeriesPriceYuan,
    string EpisodePriceYuan,
    string BroadcastDate,
    int ProductionYear,
    IReadOnlyList<string> TagLabels,
    IReadOnlyList<string> PlotLabels,
    string CoverFile,
    string EpisodeCoverFile,
    IReadOnlyList<string> ProjectImageFiles,
    string CommitmentFile,
    int EpisodeCount);

public sealed class KuaishouSeriesCatalog
{
    private readonly Dictionary<string, int> _tags;
    private readonly Dictionary<string, int> _plots;

    private KuaishouSeriesCatalog(Dictionary<string, int> tags, Dictionary<string, int> plots)
    {
        _tags = tags;
        _plots = plots;
    }

    public IReadOnlyCollection<string> TagNames => _tags.Keys;
    public IReadOnlyCollection<string> PlotNames => _plots.Keys;

    public static KuaishouSeriesCatalog Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement;
        if (data.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object)
            data = nested;
        return new KuaishouSeriesCatalog(
            ReadPairs(data, "miniSeriesTagIdList", "tagId", "tagName"),
            ReadPairs(data, "plotList", "plotId", "plotDesc"));
    }

    public int RequireTag(string label)
    {
        if (_tags.TryGetValue(label.Trim(), out var id)) return id;
        throw new InvalidOperationException($"短剧分类未找到“{label}”。可选：{string.Join('、', _tags.Keys)}。");
    }

    public int RequirePlot(string label)
    {
        if (_plots.TryGetValue(label.Trim(), out var id)) return id;
        throw new InvalidOperationException($"短剧情节未找到“{label}”。可选：{string.Join('、', _plots.Keys)}。");
    }

    private static Dictionary<string, int> ReadPairs(JsonElement data, string arrayName, string idName, string labelName)
    {
        var pairs = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!data.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            return pairs;
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty(labelName, out var labelElement) || labelElement.ValueKind != JsonValueKind.String)
                continue;
            var label = labelElement.GetString()?.Trim() ?? string.Empty;
            if (label.Length == 0 || !TryReadInt(item, idName, out var id)) continue;
            pairs.TryAdd(label, id);
        }

        return pairs;
    }

    private static bool TryReadInt(JsonElement item, string name, out int value)
    {
        value = 0;
        if (!item.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value)) return true;
        return element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value);
    }
}

public static class KuaishouSeriesDraft
{
    public const int OnsiteProjectMaterialType = 4;
    public const int DescriptionMinLength = 100;
    public const int DescriptionMaxLength = 400;

    public static int SeriesContentTypeCode(string label) => label.Trim() switch
    {
        "短剧" => 1,
        "漫剧" => 2,
        _ => throw new InvalidOperationException($"内容类型无法对应接口代码：“{label}”。"),
    };

    public static int? ProductMethodCode(string label, int seriesContentType)
    {
        if (seriesContentType != 2) return null;
        var value = KuaishouFormChoices.CanonicalValue(KuaishouFormChoices.ComicMethods, label, "3");
        if (!int.TryParse(value, out var code))
            throw new InvalidOperationException($"漫剧制作方式无法对应接口代码：“{label}”。");
        return code;
    }

    public static int ChoiceCode(IReadOnlyList<KuaishouFormChoice> choices, string label, string fallback, string field)
    {
        var value = KuaishouFormChoices.CanonicalValue(choices, label, fallback);
        if (int.TryParse(value, out var code)) return code;
        throw new InvalidOperationException($"{field}无法对应接口代码：“{label}”。");
    }

    public static int AudienceGenderCode(string label)
    {
        var text = label.Trim() switch
        {
            "男" => "男频",
            "女" => "女频",
            var other => other,
        };
        return text switch
        {
            "男频" => 1,
            "女频" => 2,
            "不限" => 3,
            _ => throw new InvalidOperationException($"男女频道无法对应接口代码：“{label}”。"),
        };
    }

    public static int ToCents(string yuan, string field)
    {
        if (!decimal.TryParse(yuan, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) &&
            !decimal.TryParse(yuan, NumberStyles.Number, CultureInfo.CurrentCulture, out value))
            throw new InvalidOperationException($"{field}“{yuan}”无法识别。");
        var cents = (int)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
        if (cents <= 0)
            throw new InvalidOperationException($"{field}“{yuan}”必须大于 0。");
        return cents;
    }

    /// <summary>
    /// 快手日期控件按东八区当天零点保存时间戳。2026-01-01 对应 1767196800000。
    /// </summary>
    public static long ChinaMidnightUnixMilliseconds(DateTime date)
    {
        var day = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(day, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
    }

    public static IReadOnlyList<string> FieldLines(KuaishouSeriesDraftInput input, KuaishouSeriesFieldLog fields)
    {
        var (director, screenwriter, producer) = CrewOf(input);
        var lines = new List<string>
        {
            Line("短剧名称", input.Title.Trim()),
            Line("短剧简介", SingleLine(input.Description)),
            Line("内容类型", fields.ContentTypeLabel),
        };
        if (input.ProductMethod is not null)
            lines.Add(Line("漫剧制作方式", fields.ProductMethodLabel));
        lines.Add(Line("短剧分类", Join(fields.TagLabels)));
        lines.Add(Line("情节", Join(fields.PlotLabels)));
        lines.Add(Line("男女频道", AudienceLabel(input.AudienceGender)));
        lines.Add(Line("作者声明", fields.AuthorDeclarationLabel));
        lines.Add(Line("版权证明类型", fields.CopyrightProofTypeLabel));
        lines.Add(Line("制作形式", input.ProductionForm));
        lines.Add(Line("出品年份", fields.ProductionYear.ToString(CultureInfo.InvariantCulture)));
        lines.Add(Line("单集平均时长", $"{input.EpisodeAvgTime.ToString(CultureInfo.InvariantCulture)} 分钟"));
        lines.Add(Line("制作成本", $"{input.ProductionCost.ToString(CultureInfo.InvariantCulture)} 万元"));
        lines.Add(Line("售卖类型", fields.SaleTypeLabel));
        lines.Add(Line("免费集数", input.FreeEpisodeCount.ToString(CultureInfo.InvariantCulture)));
        if (input.SaleType == 3)
            lines.Add(Line("广告解锁集数", (input.UnlockCount ?? 0).ToString(CultureInfo.InvariantCulture)));
        lines.Add(Line("小额解锁", YesNo(input.SmallAmountUnlock)));
        lines.Add(Line("单集价格", $"{fields.EpisodePriceYuan} 元"));
        lines.Add(Line("整剧价格", $"{fields.SeriesPriceYuan} 元"));
        lines.Add(Line("完结", YesNo(input.UpdateEnd)));
        lines.Add(Line("备案号", "无"));
        lines.Add(Line("播出平台", input.BroadcastPlatform));
        lines.Add(Line("播出途径", input.BroadcastChannel));
        lines.Add(Line("播出时间", fields.BroadcastDate));
        lines.Add(Line("制作机构", input.ProductionOrg));
        lines.Add(Line("导演", PersonText(director)));
        lines.Add(Line("编剧", PersonText(screenwriter)));
        lines.Add(Line("制片人", PersonText(producer)));
        for (var index = 0; index < input.Actors.Count; index++)
        {
            var actor = input.Actors[index];
            lines.Add(Line($"演员{index + 1}", $"{actor.Name} / {actor.Gender} / {actor.Role}"));
        }
        lines.Add(Line("集数", fields.EpisodeCount.ToString(CultureInfo.InvariantCulture)));
        lines.Add(Line("短剧封面", FileName(fields.CoverFile)));
        lines.Add(Line("单集封面", FileName(fields.EpisodeCoverFile)));
        lines.Add(Line("工程图", Join(fields.ProjectImageFiles.Select(FileName))));
        lines.Add(Line("承诺函", FileName(fields.CommitmentFile)));
        return lines;
    }

    public static string Build(KuaishouSeriesDraftInput input)
    {
        var title = input.Title.Trim();
        if (title.Length == 0) throw new InvalidOperationException("短剧标题为空。");
        if (title.Length > 30) title = title[..30];
        var description = input.Description.Trim();
        if (description.Length < DescriptionMinLength || description.Length > DescriptionMaxLength)
            throw new InvalidOperationException($"短剧简介需要 {DescriptionMinLength} 到 {DescriptionMaxLength} 个字，当前 {description.Length} 个字。");
        if (input.TagIds.Count is < 1 or > 3)
            throw new InvalidOperationException($"短剧分类需要 1 到 3 个，当前 {input.TagIds.Count} 个。");
        if (input.Enterprise && input.PlotIds.Count is < 1 or > 3)
            throw new InvalidOperationException($"短剧情节需要 1 到 3 个，当前 {input.PlotIds.Count} 个。");
        if (input.Enterprise && input.AudienceGender is null)
            throw new InvalidOperationException("请选择男女频道。");
        if (input.EpisodeTitles.Count < 1)
            throw new InvalidOperationException("没有可设置的剧集视频，无法确定单集信息。");
        if (!input.Creating && input.EpisodeIds.Count != input.EpisodeTitles.Count)
            throw new InvalidOperationException($"已有短剧有 {input.EpisodeIds.Count} 集，本地有 {input.EpisodeTitles.Count} 集，已停止更新。");
        if (input.ProjectImageKeys.Count != 4)
            throw new InvalidOperationException($"短剧工程图需要 4 张，当前 {input.ProjectImageKeys.Count} 张。");
        if (string.IsNullOrWhiteSpace(input.CoverImgKey) || string.IsNullOrWhiteSpace(input.EpisodeCoverImgKey))
            throw new InvalidOperationException("封面上传没有返回 imgKey。");
        if (string.IsNullOrWhiteSpace(input.CommitmentKey))
            throw new InvalidOperationException("承诺函上传没有返回 imgKey。");

        var payload = new JsonObject
        {
            ["coverImgKey"] = input.CoverImgKey,
            ["miniSeriesTitle"] = title,
            ["description"] = description,
            ["updateEnd"] = input.UpdateEnd,
            ["saleType"] = input.SaleType,
            ["priceCent"] = input.PriceCents,
            ["episodes"] = Episodes(input),
            ["hasFilingNo"] = false,
            ["miniSeriesTagIdList"] = Ints(input.TagIds),
            ["smallAmountUnlock"] = input.SmallAmountUnlock,
            ["filingNoType"] = 1,
            ["productionCost"] = input.ProductionCost,
            ["episodeAvgTime"] = input.EpisodeAvgTime,
            ["productionOrg"] = input.ProductionOrg,
            ["revolutionHistorySpecialTheme"] = false,
            ["displayInfoList"] = new JsonArray
            {
                new JsonObject
                {
                    ["displayPlatform"] = input.BroadcastPlatform,
                    ["displayChannel"] = input.BroadcastChannel,
                    ["displayDate"] = input.DisplayDate,
                },
            },
            ["productionDate"] = input.ProductionDate,
            ["productionForm"] = input.ProductionForm,
            ["checkNewFilingParamFlag"] = true,
            ["enableAuthorDeclaration"] = true,
            ["authorDeclaration"] = input.AuthorDeclaration,
            ["copyrightProofType"] = input.CopyrightProofType,
            ["authorizedCopyrightMaterialKeyList"] = new JsonArray(),
            ["copyrightProofMaterialInfoList"] = new JsonArray
            {
                new JsonObject
                {
                    ["materialType"] = OnsiteProjectMaterialType,
                    ["fileKeyList"] = Strings(input.ProjectImageKeys),
                    ["commitmentLetterKeyList"] = new JsonArray(input.CommitmentKey),
                },
            },
            ["enableCopyrightProofNewAttribute"] = true,
            ["hasCopyrightProof"] = true,
            ["syncProfile"] = true,
            ["seriesContentType"] = input.SeriesContentType,
        };
        if (input.SaleType == 3 && input.UnlockCount is int unlock)
            payload["unlockCount"] = unlock;
        if (input.ProductMethod is int method)
            payload["productMethod"] = method;
        if (!input.Creating)
            payload["miniSeriesId"] = long.Parse(input.MiniSeriesId, CultureInfo.InvariantCulture);
        AddPeople(payload, input);
        if (input.Enterprise || input.PlotIds.Count > 0)
        {
            if (input.AudienceGender is not int gender)
                throw new InvalidOperationException("请选择男女频道。");
            payload["checkNewSdpaFieldsFlag"] = true;
            payload["plotList"] = Ints(input.PlotIds);
            payload["audienceGender"] = gender;
        }

        return payload.ToJsonString();
    }

    public static void EnsureSuccess(JsonElement root)
    {
        if (IsSuccess(root)) return;
        var message = ReadMessage(root);
        if (KuaishouDailyUploadLimit.Matches(message))
            throw new KuaishouDailyUploadLimitException();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? "快手接口没有返回成功结果。"
            : message.Length <= 300 ? message : message[..300]);
    }

    public static bool IsSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result))
            return root.TryGetProperty("miniSeriesId", out _) || root.TryGetProperty("imgKey", out _);
        if (result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var code))
            return code == 1;
        return result.ValueKind == JsonValueKind.String && result.GetString() == "1";
    }

    public static string ReadMessage(JsonElement root)
    {
        foreach (var name in new[] { "error_msg", "errorMsg", "message", "msg" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim() ?? string.Empty;
                if (text.Length > 0) return text;
            }
        }

        return string.Empty;
    }

    public static string ReadCreatedId(JsonElement root)
    {
        foreach (var element in new[] { root, DataOf(root) })
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("miniSeriesId", out var id))
                continue;
            var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }

        return string.Empty;
    }

    public static string ReadImageKey(JsonElement root)
    {
        foreach (var element in new[] { root, DataOf(root) })
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("imgKey", out var key) &&
                key.ValueKind == JsonValueKind.String)
            {
                var text = key.GetString()?.Trim() ?? string.Empty;
                if (text.Length > 0) return text;
            }
        }

        return string.Empty;
    }

    public readonly record struct ListedSeries(string Id, int AuditStatus);

    public static IReadOnlyList<ListedSeries> ReadMatchingSeries(JsonElement root, string title)
    {
        var matches = new List<ListedSeries>();
        var rows = Rows(root).ToArray();
        if (rows.Length == 0) rows = Rows(root, "list").ToArray();
        foreach (var row in rows)
        {
            var name = FirstString(row, "courseName", "miniSeriesTitle");
            if (!string.Equals(name, title.Trim(), StringComparison.Ordinal)) continue;
            var id = FirstString(row, "miniSeriesId");
            if (id.Length == 0 || matches.Any(match => match.Id == id)) continue;
            var status = row.TryGetProperty("auditStatus", out var audit) && audit.TryGetInt32(out var value) ? value : -1;
            matches.Add(new ListedSeries(id, status));
        }

        return matches;
    }

    public static IReadOnlyList<string> ReadEpisodeIds(JsonElement root)
    {
        var rows = new List<(int Order, int Index, string Id)>();
        var index = 0;
        foreach (var row in Rows(root, "episodeList", "seriesList"))
        {
            var id = FirstString(row, "episodeId");
            if (id.Length == 0)
            {
                index++;
                continue;
            }
            var order = row.TryGetProperty("orderNo", out var orderNo) && orderNo.TryGetInt32(out var value)
                ? value
                : index;
            rows.Add((order, index, id));
            index++;
        }

        return rows
            .OrderBy(row => row.Order)
            .ThenBy(row => row.Index)
            .Select(row => row.Id)
            .ToArray();
    }

    private readonly record struct CrewMember(string Name, string Gender);

    private static (CrewMember Director, CrewMember Screenwriter, CrewMember Producer) CrewOf(KuaishouSeriesDraftInput input)
    {
        if (!input.Enterprise)
        {
            var credit = new CrewMember(input.CreditName, input.CreditGender);
            return (credit, credit, credit);
        }

        return (
            new CrewMember(input.DirectorName, input.DirectorGender),
            new CrewMember(input.ScreenwriterName, input.ScreenwriterGender),
            new CrewMember(input.ProducerName, input.ProducerGender));
    }

    private static string Line(string name, string value) => $"{name}：{value}";

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string AudienceLabel(int? gender) => gender switch
    {
        1 => "男频",
        2 => "女频",
        3 => "不限",
        _ => "未选择",
    };

    private static string PersonText(CrewMember person) => $"{person.Name.Trim()} / {person.Gender.Trim()}";

    private static string Join(IEnumerable<string> values)
    {
        var text = string.Join("、", values.Select(value => value.Trim()).Where(value => value.Length > 0));
        return text.Length == 0 ? "未选择" : text;
    }

    private static string SingleLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FileName(string path) =>
        string.IsNullOrWhiteSpace(path) ? "未选择" : Path.GetFileName(path.Trim());

    private static void AddPeople(JsonObject payload, KuaishouSeriesDraftInput input)
    {
        var crew = CrewOf(input);
        var directorName = crew.Director.Name;
        var directorGender = crew.Director.Gender;
        var screenwriterName = crew.Screenwriter.Name;
        var screenwriterGender = crew.Screenwriter.Gender;
        var producerName = crew.Producer.Name;
        var producerGender = crew.Producer.Gender;
        if (input.Enterprise && string.IsNullOrWhiteSpace(directorName))
            throw new InvalidOperationException("请填写导演姓名。");
        if (input.Actors.Count < 2)
            throw new InvalidOperationException("演员需要 2 位。");

        var actors = new JsonArray();
        for (var index = 0; index < input.Actors.Count; index++)
        {
            var actor = input.Actors[index];
            if (actor.Gender is not ("男" or "女"))
                throw new InvalidOperationException($"第 {index + 1} 位演员性别未设置，当前为“{actor.Gender}”。");
            actors.Add(Person(actor.Name, actor.Gender, actor.Role));
        }

        payload["mainActorList"] = actors;
        if (!string.IsNullOrWhiteSpace(directorName))
        {
            RequireGender(directorGender, "导演");
            payload["directorList"] = new JsonArray(directorName.Trim());
            payload["directorInfoList"] = new JsonArray(Person(directorName, directorGender, role: null));
        }
        if (!string.IsNullOrWhiteSpace(screenwriterName))
        {
            RequireGender(screenwriterGender, "编剧");
            payload["screenwriterInfoList"] = new JsonArray(Person(screenwriterName, screenwriterGender, role: null));
        }
        if (!string.IsNullOrWhiteSpace(producerName))
        {
            RequireGender(producerGender, "制片人");
            payload["producer"] = producerName.Trim();
            payload["producerInfo"] = Person(producerName, producerGender, role: null);
        }
    }

    private static void RequireGender(string gender, string role)
    {
        if (gender is not ("男" or "女"))
            throw new InvalidOperationException($"{role}性别未设置，当前为“{gender}”。");
    }

    private static JsonObject Person(string name, string gender, string? role)
    {
        var person = new JsonObject
        {
            ["gender"] = gender,
            ["birthday"] = 0,
            ["country"] = "",
            ["company"] = "",
        };
        if (role is null)
            person["name"] = name.Trim();
        else
        {
            person["actorName"] = name.Trim();
            person["actorRole"] = role.Trim();
        }

        return person;
    }

    private static JsonArray Episodes(KuaishouSeriesDraftInput input)
    {
        var episodes = new JsonArray();
        for (var index = 0; index < input.EpisodeTitles.Count; index++)
        {
            var episode = new JsonObject
            {
                ["priceCent"] = input.EpisodePriceCents,
                ["coverImgKey"] = input.EpisodeCoverImgKey,
                ["episodeTitle"] = input.EpisodeTitles[index],
                ["free"] = index < input.FreeEpisodeCount,
            };
            if (!input.Creating)
                episode["episodeId"] = long.Parse(input.EpisodeIds[index], CultureInfo.InvariantCulture);
            episodes.Add(episode);
        }

        return episodes;
    }

    private static JsonArray Ints(IReadOnlyList<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonElement DataOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data
            : default;

    private static IEnumerable<JsonElement> Rows(JsonElement root, params string[] names)
    {
        var data = DataOf(root);
        if (data.ValueKind != JsonValueKind.Object) yield break;
        if (names.Length == 0)
        {
            foreach (var row in ArrayOf(data, "data")) yield return row;
            yield break;
        }

        foreach (var name in names)
        {
            var rows = ArrayOf(data, name).ToArray();
            if (rows.Length == 0) continue;
            foreach (var row in rows) yield return row;
            yield break;
        }
    }

    private static IEnumerable<JsonElement> ArrayOf(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object) yield return item;
    }

    private static string FirstString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }

        return string.Empty;
    }
}
