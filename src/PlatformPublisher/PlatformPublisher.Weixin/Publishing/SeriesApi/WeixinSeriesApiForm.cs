using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Weixin.Publishing;

internal sealed record WeixinSeriesApiForm(
    string Title, string Intro, string Producer, string Tagline, int EpisodeCount, int TrialCount,
    int DramaType, int RevenueType, int CopyrightRole, bool AiDeclared, int? CostWan, string? PermitNumber,
    IReadOnlyList<WeixinSeriesApiActor> Actors)
{
    private string QualificationLabel => CostWan.HasValue ? "剧目资质" : "资质证明";

    public static WeixinSeriesApiForm Create(JsonElement first, IReadOnlyList<JsonElement> actions,
        IReadOnlyList<WeixinSeriesApiFile> files)
    {
        var fields = new Dictionary<string, string>();
        var choices = new Dictionary<string, string>();
        var agreed = false;
        var aiDeclared = false;
        foreach (var action in actions)
        {
            var type = action.Text("type");
            var label = WeixinSeriesApiProject.Label(action);
            if (type == "fill")
            {
                if (label.Length == 0)
                {
                    var selector = action.Text("selector");
                    if (selector.Contains("制作成本") || selector.Contains("成本"))
                        label = "制作成本";
                    else if (selector.Contains("备案号") || selector.Contains("发行许可证号"))
                        label = "备案号";
                }

                fields[label] = action.Text("value");
            }
            else if (type == "choose")
                choices[label] = action.Text("option_text");
            else if (type == "set_checked" && label == "AI内容声明")
                aiDeclared = !action.IsFalse("enabled");
            else if (label == "我已知悉并同意" && type is "click" or "set_checked")
                agreed = type == "click" || !action.IsFalse("enabled");
        }

        if (!agreed)
            throw new InvalidOperationException("提审配置缺少「我已知悉并同意」确认。");

        string Field(string name) => fields.GetValueOrDefault(name, string.Empty);
        string Choice(string name, string fallback = "") => choices.GetValueOrDefault(name, fallback);
        string Required(string name) => Field(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"提审缺少「{name}」。");
        int Number(string name, int maximum) => WeixinSeriesApiProject.ParseNumber(Field(name), name, maximum);

        var title = Required("剧目名称");
        var intro = Required("剧目简介");
        var producer = Required("制作方名称");
        var tagline = Field("推荐语");
        if (intro.Length > 100 || tagline.Length > 15)
            throw new InvalidOperationException("接口提审的剧目简介最多 100 字，推荐语最多 15 字。");

        var dramaType = Choice("剧目类型", "真人") switch
        {
            "漫剧" => 1,
            "真人" => 2,
            "数字真人" => 3,
            var value => throw InvalidChoice("剧目类型", value),
        };
        var revenue = Choice("变现类型") switch
        {
            "" or "不操作" or "不操作(跳过)" or "IAA广告变现" => 1,
            "IAP付费变现" or "付费解锁" => 2,
            var value => throw InvalidChoice("变现类型", value),
        };
        var copyright = Choice("提审身份") switch
        {
            "" or "不操作" or "不操作(跳过)" or "剧目制作方" => 1,
            "剧目发行方" or "版权方/授权播出方" => 2,
            var value => throw InvalidChoice("提审身份", value),
        };
        int? cost = null;
        string? permit = null;
        switch (Choice("剧目资质"))
        {
            case "" or "其他微短剧":
                cost = Number("制作成本", 99);
                break;
            case "重点/普通微短剧" or "重点微短剧" or "普通微短剧":
                permit = new[] { Field("备案号"), Field("发行许可证号"), Field("网络剧片发行许可证号") }
                    .FirstOrDefault(value => value.Length > 0)
                    ?? throw new InvalidOperationException("重点/普通微短剧提审需要发行许可证号或备案号。");
                break;
            case var value:
                throw InvalidChoice("剧目资质", value);
        }

        var actors = dramaType == 2
            ? first.Get("actors").Items().Select(actor => new WeixinSeriesApiActor(
                actor.Text("name"), actor.Text("role"), actor.Text("profile"))).ToArray()
            : [];
        if (dramaType == 2 && (actors.Length < 2 || actors.Any(actor =>
                actor.Name.Length == 0 || actor.Role.Length == 0 || actor.Profile.Length == 0)))
            throw new InvalidOperationException("真人剧提审至少需要两位演员的姓名、角色、简介和照片。");

        var count = Number("总集数", 1000);
        var form = new WeixinSeriesApiForm(title, intro, producer, tagline, count, Number("试看集数", count),
            dramaType, revenue, copyright, aiDeclared, cost, permit, actors);
        form.ValidateFiles(files);
        return form;
    }

    private void ValidateFiles(IReadOnlyList<WeixinSeriesApiFile> files)
    {
        var episodes = files.Where(file => file.Episode.HasValue).Select(file => file.Episode!.Value);
        if (!episodes.SequenceEqual(Enumerable.Range(1, EpisodeCount)))
            throw new InvalidOperationException("自动提审要求视频从第 1 集起连续，且与总集数一致。");

        var labels = files.Where(file => !file.Episode.HasValue).ToLookup(file => file.Label);
        var required = new List<string> { "剧目海报", "剧目制作证明材料", QualificationLabel };
        if (CopyrightRole == 2)
            required.Add("版权采买&播出授权证明材料");
        if (AiDeclared)
        {
            required.Add("AI制作证明");
            if (labels["AI制作证明"].Count() > 3)
                throw new InvalidOperationException("AI制作证明最多上传 3 个文件。");
        }
        foreach (var label in required)
        {
            if (!labels.Contains(label))
                throw new InvalidOperationException($"提审缺少「{label}」。");
        }
        if (Actors.Count > 0 && Actors.Count != labels["演员照片"].Count())
            throw new InvalidOperationException("真人剧演员照片数量须与演员资料数量一致。");
    }

    public JsonObject BuildPayload(IReadOnlyList<WeixinSeriesApiReceipt> receipts)
    {
        var materials = receipts.Where(item => !item.File.Episode.HasValue).ToLookup(item => item.File.Label);
        var ids = receipts.Where(item => item.File.Episode.HasValue).Select(item =>
            ulong.TryParse(item.MediaId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id : throw new InvalidOperationException($"第 {item.File.Episode} 集缺少有效的视频素材编号。")).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new InvalidOperationException("不同集数对应了相同的视频素材编号。");

        JsonArray MaterialIds(string label) =>
            new(materials[label].Select(item => (JsonNode?)JsonValue.Create(item.MediaId)).ToArray());
        string LastId(string label) => materials[label].LastOrDefault()?.MediaId ?? "";

        var actors = Actors.Zip(materials["演员照片"], (actor, photo) => (JsonNode?)new JsonObject
        {
            ["name"] = actor.Name, ["role"] = actor.Role, ["profile"] = actor.Profile,
            ["photo"] = photo.MediaId, ["photoDetail"] = new JsonArray(),
        }).ToArray();
        var aiProof = AiDeclared
            ? new JsonArray(materials["AI制作证明"].Select(item => (JsonNode?)new JsonObject
            {
                ["mediaId"] = item.MediaId,
                ["type"] = Path.GetExtension(item.File.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? 2 : 1,
            }).ToArray())
            : new JsonArray();
        var qualification = CostWan is { } cost
            ? new JsonObject { ["type"] = 2, ["costOfProduction"] = cost, ["costCommitmentLetter"] = LastId(QualificationLabel) }
            : new JsonObject { ["type"] = 1, ["qualificationCertificate"] = LastId(QualificationLabel) };
        var copyright = new JsonObject
        {
            ["copyrightRole"] = CopyrightRole, ["copyrightVerification"] = 1, ["applyForCopyrightProtection"] = 1,
            ["proofOfProduction"] = MaterialIds("剧目制作证明材料"),
        };
        if (materials.Contains("版权采买&播出授权证明材料"))
            copyright["purchaseOrBroadcastAuthorizationCertificate"] = MaterialIds("版权采买&播出授权证明材料");

        return new JsonObject
        {
            ["name"] = Title, ["description"] = Intro, ["producer"] = Producer,
            ["mediaCount"] = EpisodeCount, ["freeMediaCount"] = TrialCount, ["dramaType"] = DramaType,
            ["contentInfo"] = new JsonObject { ["finderExtra"] = new JsonObject { ["revenueStrategy"] = RevenueType } },
            ["reportNumber"] = PermitNumber, ["expedited"] = 0, ["replaceMediaItem"] = new JsonArray(),
            ["coverMaterialId"] = LastId("剧目海报"),
            ["mediaList"] = new JsonObject
            {
                ["mediaId"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["urlUploadList"] = new JsonArray(),
            },
            ["extraInfo"] = new JsonObject
            {
                ["otherInfo"] = new JsonObject
                {
                    ["recommendations"] = Tagline, ["alternateName"] = "", ["otherMaterial"] = "",
                    ["contentDeclared"] = AiDeclared ? 1 : 0, ["promotionPoster"] = LastId("推广海报"),
                    ["actorInfos"] = new JsonObject { ["actor"] = actors.Length == 0 ? null : new JsonArray(actors) },
                    ["aiSoftwareOwnershipProofList"] = aiProof,
                },
                ["copyright"] = copyright, ["qualificationInfo"] = qualification,
            },
        };
    }

    private static InvalidOperationException InvalidChoice(string field, string value) =>
        new($"接口提审暂不支持「{field}」的选项「{value}」。");
}

internal sealed record WeixinSeriesApiActor(string Name, string Role, string Profile);
