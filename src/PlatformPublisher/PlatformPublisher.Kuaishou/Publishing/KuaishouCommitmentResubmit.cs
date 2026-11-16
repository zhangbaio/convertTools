using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 已上架且发布成功、但承诺函被提示未按格式撰写或未加盖公章的短剧。只识别，不改内容。
/// </summary>
public static class KuaishouCommitmentResubmit
{
    public const string QueryListUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/queryList";
    public const string BaseInfoUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/baseInfo";
    public const string EpisodeListUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/episodeList";
    public const string UpdateUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/update";
    public const string StartupUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/pc/startup";
    public const string TeacherInfoUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/pc/teacher/info";
    public const int ListedSellingStatus = 1;
    public const int PublishedAuditStatus = 3;
    public const int PageSize = 20;

    public readonly record struct Target(string SeriesId, string Title, string Reason);

    public static string QueryBody(int pageNum, int pageSize) =>
        JsonSerializer.Serialize(new
        {
            pageNum,
            pageSize,
            episodeTitle = "",
            sellingStatus = ListedSellingStatus,
            queryType = 0,
            sortParam = new { },
            miniSeriesTitle = "",
            auditStatus = PublishedAuditStatus,
            saleType = (int?)null,
            seriesIdList = Array.Empty<int>(),
            createDateFrom = "",
            createDateTo = "",
            sourceType4Filter = (int?)null,
        });

    public static string SeriesIdBody(string seriesId) =>
        JsonSerializer.Serialize(new { miniSeriesId = seriesId.Trim() });

    public static string SubmitBody(string seriesId) => SeriesIdBody(seriesId);

    public static string EpisodeListBody(string seriesId) =>
        JsonSerializer.Serialize(new { miniSeriesId = seriesId.Trim(), allLesson = true });

    public readonly record struct SaveContext(bool CopyrightValidation, bool TubeSupportIap, bool AuthorDeclaration, bool Screenwriter, bool CopyrightProofNewAttribute, bool NewSdpaFields, bool NovelToAnimal, int BizIdentity);

    public static SaveContext ReadSaveContext(JsonElement startup, JsonElement teacher)
    {
        var flags = Data(startup);
        var account = Data(teacher);
        return new SaveContext(
            Bool(flags, "enableCopyrightValidation") == true,
            Bool(flags, "enableTubeSupportIap") == true,
            Bool(flags, "enableAuthorDeclaration") == true,
            Bool(flags, "enableScreenwriterAttributeFlag") == true,
            Bool(flags, "enableCopyrightProofNewAttribute") == true,
            Bool(flags, "enableNewSdpaFieldsEdit") == true,
            Bool(flags, "enableNovelToAnimalSeries") == true,
            Int(account, "bizIdentity"));
    }

    public static bool CanModifyVideo(JsonElement updateResponse)
    {
        var data = Data(updateResponse);
        return Bool(data, "canModifyVideo") == true || Bool(updateResponse, "canModifyVideo") == true;
    }

    public static string UpdateBody(JsonElement baseInfo, JsonElement episodeList, SaveContext context, string seriesId)
    {
        var info = Data(baseInfo);
        var episodes = EpisodeRows(episodeList);
        var body = new JsonObject
        {
            ["copyrightMaterialValidationFlag"] = context.CopyrightValidation,
            ["coverImgKey"] = Text(info, "coverImgKey"),
            ["miniSeriesTitle"] = FirstText(info, "courseName", "miniSeriesTitle"),
            ["description"] = FirstText(info, "courseDescription", "description"),
            ["updateEnd"] = Bool(info, "updateEnd") == true,
            ["saleType"] = Int(info, "saleType"),
            ["priceCent"] = Cents(info, "price"),
            ["episodes"] = EpisodeBody(episodes),
            ["hasFilingNo"] = Bool(info, "hasFilingNo") == true,
            ["miniSeriesTagIdList"] = TagIds(info),
            ["filingNoType"] = Int(info, "filingNoType"),
            ["smallAmountUnlock"] = Bool(info, "smallAmountUnlock") == true,
        };
        if (Int(info, "saleType") == 3)
            body["unlockCount"] = Int(info, "unlockCount");
        if (context.BizIdentity == 3)
            body["unlockStart"] = UnlockStart(info);
        if (Bool(info, "hasFilingNo") == true)
            body["filingNo"] = Text(info, "filingNo");
        else
            CopyUnfiled(info, body);
        body["checkNewFilingParamFlag"] = Bool(info, "checkNewFilingParamFlag") == true;
        if (context.Screenwriter && Bool(info, "hasFilingNo") != true && HasName(info, "screenwriterInfoList"))
            body["screenwriterInfoList"] = Clone(info, "screenwriterInfoList");
        body["enableAuthorDeclaration"] = context.AuthorDeclaration;
        if (context.AuthorDeclaration)
            body["authorDeclaration"] = Int(info, "authorDeclaration");
        body["copyrightProofType"] = Int(info, "copyrightProofType");
        body["copyrightProofValidStartTime"] = Clone(info, "copyrightProofValidStartTime");
        body["copyrightProofValidEndTime"] = Clone(info, "copyrightProofValidEndTime");
        body["hasSubAuthorizationRight"] = Clone(info, "hasSubAuthorizationRight");
        body["authorizedCopyrightMaterialKeyList"] = FileKeys(info, "authorizedCopyrightMaterialList");
        var materials = CopyrightMaterials(info);
        body["copyrightProofMaterialInfoList"] = materials;
        body["enableCopyrightProofNewAttribute"] = context.CopyrightProofNewAttribute;
        if (!context.CopyrightProofNewAttribute && Int(info, "copyrightProofType") > 0 && materials.Count > 0)
            body["hasCopyrightProof"] = true;
        else if (!context.CopyrightProofNewAttribute && Int(info, "copyrightProofType") == 0 && materials.Count == 0)
            body["hasCopyrightProof"] = false;
        if (context.NovelToAnimal)
            body["novelToAnimalSeriesInfo"] = Clone(info, "novelToAnimalSeriesInfo");
        if (context.NewSdpaFields)
        {
            body["checkNewSdpaFieldsFlag"] = Bool(info, "showSdpaFieldsEditEntrance") == true;
            body["mainActorList"] = Clone(info, "mainActorList");
            body["plotList"] = Clone(info, "plotList") ?? new JsonArray();
            body["audienceGender"] = Clone(info, "audienceGender");
        }

        body["miniSeriesId"] = seriesId.Trim();
        body["verticalSupportIap"] = context.TubeSupportIap;
        body["syncProfile"] = Bool(info, "syncProfile") == true;
        body["seriesContentType"] = Int(info, "seriesContentType");
        body["productMethod"] = Int(info, "productMethod");
        return body.ToJsonString();
    }

    public static bool IsCommitmentSealWarning(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("未按格式撰写承诺函", StringComparison.Ordinal)
            && text.Contains("未加盖机构主体公章", StringComparison.Ordinal);
    }

    public static int ReadPage(JsonElement root, ICollection<Target> matches, out int totalCount)
    {
        totalCount = 0;
        if (!TryData(root, out var data))
            return 0;
        if (data.TryGetProperty("totalCount", out var total) && total.TryGetInt32(out var count))
            totalCount = count;
        if (!data.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return 0;

        var raw = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            raw++;
            if (!IsListedAndPublished(row) || !TryReason(row, out var reason))
                continue;
            var id = ReadId(row);
            if (id.Length == 0)
                continue;
            var title = ReadString(row, "courseName");
            if (title.Length == 0)
                title = ReadString(row, "miniSeriesTitle");
            if (title.Length == 0)
                title = id;
            matches.Add(new Target(id, title, reason));
        }

        return raw;
    }

    private static bool TryData(JsonElement root, out JsonElement data)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out data) &&
            data.ValueKind == JsonValueKind.Object)
            return true;
        data = default;
        return false;
    }

    private static bool IsListedAndPublished(JsonElement row) =>
        ReadInt(row, "sellingStatus") == ListedSellingStatus &&
        ReadInt(row, "auditStatus") == PublishedAuditStatus;

    private static bool TryReason(JsonElement row, out string reason)
    {
        foreach (var name in new[] { "failReasonSummary", "checkReasonDisplayInfo", "checkReasons" })
        {
            if (!row.TryGetProperty(name, out var node))
                continue;
            foreach (var text in Strings(node))
            {
                if (!IsCommitmentSealWarning(text))
                    continue;
                reason = text.Trim();
                return true;
            }
        }

        reason = "";
        return false;
    }

    private static IEnumerable<string> Strings(JsonElement node)
    {
        var found = new List<string>();
        Collect(node, found);
        return found;
    }

    private static void Collect(JsonElement node, List<string> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                var value = node.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    found.Add(value);
                break;
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                    Collect(property.Value, found);
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    Collect(item, found);
                break;
        }
    }

    private static string ReadId(JsonElement row)
    {
        if (!row.TryGetProperty("miniSeriesId", out var id))
            return "";
        if (id.ValueKind == JsonValueKind.String)
            return id.GetString()?.Trim() ?? "";
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number))
            return number.ToString(CultureInfo.InvariantCulture);
        return "";
    }

    private static string ReadString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static int ReadInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : -1;

    private static void CopyUnfiled(JsonElement info, JsonObject body)
    {
        body["productionCost"] = Clone(info, "productionCost");
        body["episodeAvgTime"] = Clone(info, "episodeAvgTime");
        body["directorList"] = NamesOrNull(info, "directorList");
        var actors = Clone(info, "mainActorList") as JsonArray;
        body["mainActorList"] = FirstActorBlank(actors) ? null : actors;
        body["producer"] = Clone(info, "producer");
        body["productionOrg"] = Text(info, "productionOrg");
        body["productionOrgProvince"] = Clone(info, "productionOrgProvince");
        var license = Text(info, "productionLicenseNo");
        if (license == "字第号")
            body["productionLicenseNo"] = license;
        else if (TryLicense(license, out var rebuilt))
            body["productionLicenseNo"] = rebuilt;
        body["revolutionHistorySpecialTheme"] = Bool(info, "revolutionHistorySpecialTheme") == true;
        if (Bool(info, "revolutionHistorySpecialTheme") == true)
        {
            body["themeId"] = ThemeId(info);
            body["subThemeIdList"] = SubThemeIds(info);
        }

        if (Int(info, "filingNoType") == 1 && Text(info, "filingNo").Length > 0)
            body["filingNo"] = Text(info, "filingNo");
        body["displayInfoList"] = HasText(info, "displayInfoList", "displayChannel") ? Clone(info, "displayInfoList") : null;
        body["productionDate"] = Clone(info, "productionDate");
        body["posterImgKey"] = Clone(info, "posterImgKey");
        body["directorInfoList"] = HasName(info, "directorInfoList") ? Clone(info, "directorInfoList") : null;
        body["producerInfo"] = PersonName(info, "producerInfo").Length > 0 ? Clone(info, "producerInfo") : null;
        body["productionOrgTax"] = Clone(info, "productionOrgTax");
        body["productionPeriod"] = Clone(info, "productionPeriod");
        body["productionOrgCountry"] = Clone(info, "productionOrgCountry");
        body["productionForm"] = Clone(info, "productionForm");
        body["productionOrgLicenseImgKey"] = Clone(info, "productionOrgLicenseImgKey");
    }

    private static JsonElement Data(JsonElement root) =>
        TryData(root, out var data) ? data : root;

    private static JsonArray EpisodeBody(JsonElement rows)
    {
        var episodes = new JsonArray();
        if (rows.ValueKind != JsonValueKind.Array)
            return episodes;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            episodes.Add(new JsonObject
            {
                ["priceCent"] = Cents(row, "price"),
                ["episodeId"] = Clone(row, "episodeId"),
                ["coverImgKey"] = Text(row, "coverImgKey"),
                ["episodeTitle"] = Text(row, "episodeTitle"),
                ["free"] = Bool(row, "free") == true,
            });
        }

        return episodes;
    }

    private static JsonElement EpisodeRows(JsonElement root)
    {
        var data = Data(root);
        if (data.TryGetProperty("seriesList", out var series) && series.ValueKind == JsonValueKind.Array)
            return series;
        if (data.TryGetProperty("episodeList", out var episodes) && episodes.ValueKind == JsonValueKind.Array)
            return episodes;
        return default;
    }

    private static JsonArray TagIds(JsonElement info)
    {
        var tags = new JsonArray();
        if (!info.TryGetProperty("miniSeriesTagIdList", out var list) || list.ValueKind != JsonValueKind.Array)
            return tags;
        foreach (var tag in list.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.Number && tag.TryGetInt32(out var id))
                tags.Add(id);
            else if (tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("tagId", out var tagId) && tagId.TryGetInt32(out var nested))
                tags.Add(nested);
        }

        return tags;
    }

    private static JsonArray CopyrightMaterials(JsonElement info)
    {
        var rows = new JsonArray();
        if (!info.TryGetProperty("copyrightProofMaterialInfoVoList", out var list) || list.ValueKind != JsonValueKind.Array)
            return rows;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var letters = Int(item, "materialType") is 4 or 5
                ? FileKeys(item, "commitmentLetterList")
                : new JsonArray();
            rows.Add(new JsonObject
            {
                ["materialType"] = Int(item, "materialType"),
                ["fileKeyList"] = FileKeys(item, "fileList"),
                ["commitmentLetterKeyList"] = letters,
            });
        }

        return rows;
    }

    private static JsonArray FileKeys(JsonElement owner, string name)
    {
        var keys = new JsonArray();
        if (!owner.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return keys;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    keys.Add(text);
            }
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("fileKey", out var key) && key.ValueKind == JsonValueKind.String)
            {
                var text = key.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    keys.Add(text);
            }
        }

        return keys;
    }

    private static JsonNode? NamesOrNull(JsonElement info, string name)
    {
        if (!info.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        var names = new JsonArray();
        foreach (var item in list.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : "";
            if (!string.IsNullOrWhiteSpace(text))
                names.Add(text);
        }

        return names.Count == 0 ? null : names;
    }

    private static bool FirstActorBlank(JsonArray? actors)
    {
        if (actors is null || actors.Count == 0 || actors[0] is not JsonObject first)
            return true;
        var name = NodeText(first["actorName"]);
        var role = NodeText(first["actorRole"]);
        return name.Length == 0 && role.Length == 0;
    }

    private static string NodeText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? "" : "";

    private static JsonNode? ThemeId(JsonElement info)
    {
        if (info.TryGetProperty("theme", out var theme) &&
            theme.ValueKind == JsonValueKind.Object &&
            theme.TryGetProperty("themeId", out var id) &&
            id.ValueKind != JsonValueKind.Null)
            return JsonNode.Parse(id.GetRawText());
        return Clone(info, "themeId");
    }

    private static JsonArray SubThemeIds(JsonElement info)
    {
        var ids = new JsonArray();
        if (!info.TryGetProperty("subThemeList", out var list) || list.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("themeId", out var id) &&
                id.TryGetInt32(out var number))
                ids.Add(number);
        }

        return ids;
    }

    private static bool HasName(JsonElement info, string name) =>
        PersonList(info, name).Any(person => person.Length > 0);

    private static bool HasText(JsonElement info, string listName, string field)
    {
        if (!info.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && Text(item, field).Length > 0)
                return true;
        }

        return false;
    }

    private static IEnumerable<string> PersonList(JsonElement info, string name)
    {
        if (!info.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                yield return Text(item, "name");
        }
    }

    private static string PersonName(JsonElement info, string name) =>
        info.TryGetProperty(name, out var person) && person.ValueKind == JsonValueKind.Object
            ? Text(person, "name")
            : "";

    private static string UnlockStart(JsonElement info)
    {
        if (!info.TryGetProperty("unlockStart", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "0";
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrEmpty(value.GetString()) ? "0" : value.GetString()!;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number.ToString(CultureInfo.InvariantCulture);
        return "0";
    }

    private static bool TryLicense(string license, out string rebuilt)
    {
        rebuilt = "";
        var mark = license.IndexOf("字第", StringComparison.Ordinal);
        var end = license.LastIndexOf('号');
        if (mark <= 0 || end <= mark + 2)
            return false;
        rebuilt = license[..mark] + "字第" + license[(mark + 2)..end] + "号";
        return true;
    }

    private static int Cents(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        if (value.TryGetInt32(out var number))
            return number;
        return value.TryGetDouble(out var amount) ? (int)Math.Round(amount) : 0;
    }

    private static JsonNode? Clone(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return JsonNode.Parse(value.GetRawText());
    }

    private static string Text(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object &&
        owner.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string FirstText(JsonElement owner, string first, string second)
    {
        var text = Text(owner, first);
        return text.Length > 0 ? text : Text(owner, second);
    }

    private static bool? Bool(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int Int(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object &&
        owner.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : 0;
}
