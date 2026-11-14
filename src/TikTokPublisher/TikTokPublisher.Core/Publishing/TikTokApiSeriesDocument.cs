using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TikTokPublisher.Core.Publishing;

public sealed class TikTokApiSeriesShell
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? CoverUri { get; init; }
    public int TotalVideoNum { get; init; }
    public int ProfilePreviewEpisodes { get; init; } = 3;
    public int FreePreviewEpisodes { get; init; } = 3;
    public bool AnchorPromotionEnabled { get; init; } = true;
    public bool IsAiSeries { get; init; }
    public string? SourceLanguage { get; init; }
    public IReadOnlyList<string> ThemeTagIds { get; init; } = [];
    public string? AudienceTagId { get; init; }
    public bool ConsignmentEnabled { get; init; }
    public string? BusinessContractId { get; init; }
    public string? PriceInUsd { get; init; }
    public bool IsOriginalRightsHolder { get; init; } = true;
    public bool IsAdaptation { get; init; }
    public IReadOnlyList<TikTokApiCopyrightFile>? CopyrightFiles { get; init; }
}

public readonly record struct TikTokApiCopyrightFile(int MaterialType, string Uri, string FileName);

public static class TikTokApiSeriesDocument
{
    public static string BuildCreateBody(string title, string? businessContractId)
    {
        var contract = new JsonObject { ["title"] = title?.Trim() ?? "" };
        var contractId = businessContractId?.Trim() ?? "";
        if (contractId.Length > 0)
            contract["contractId"] = contractId;
        return new JsonObject { ["contract"] = contract }.ToJsonString();
    }

    public static string BuildSubmitBody(string seriesId) =>
        new JsonObject
        {
            ["contract"] = new JsonObject { ["contractId"] = seriesId },
        }.ToJsonString();

    public static string? FindTagId(JsonElement root, string groupName, IReadOnlyCollection<string> labels)
    {
        if (!TryFindProperty(root, groupName, out var group))
            return null;
        return MatchTag(group, labels);
    }

    public static bool IsPlatformContractId(string? value)
    {
        var id = value?.Trim() ?? "";
        return id.Length is >= 8 and <= 24 && id.All(char.IsDigit);
    }

    public readonly record struct TikTokBusinessContract(string Id, string Title);

    /// <summary>
    /// 新建页合同下拉来自 contract/list 的 contractList，值是 contractedId。
    /// contractedId 为 0 或 1 是页面占位，不能拿去建剧。
    /// </summary>
    public static TikTokBusinessContract? ReadFirstBusinessContract(JsonElement root)
    {
        foreach (var list in ContractLists(root))
        {
            foreach (var item in list.EnumerateArray())
            {
                var contract = ReadBusinessContract(item);
                if (contract is not null)
                    return contract;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> ContractLists(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;
        if (root.TryGetProperty("contractList", out var list) && list.ValueKind == JsonValueKind.Array)
            yield return list;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("contractList", out var nested) && nested.ValueKind == JsonValueKind.Array)
            yield return nested;
    }

    private static TikTokBusinessContract? ReadBusinessContract(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in new[] { "contractedId", "contractId" })
        {
            if (!item.TryGetProperty(name, out var idElement))
                continue;
            var id = idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()?.Trim() ?? ""
                : idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetRawText()
                    : "";
            if (!IsPlatformContractId(id))
                continue;
            var title = item.TryGetProperty("contractTitle", out var titleElement) &&
                        titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString()?.Trim() ?? ""
                : "";
            return new TikTokBusinessContract(id, title);
        }

        return null;
    }

    /// <summary>
    /// 账号里填的可能是 CT 开头的合同编号。建剧只认 contractList 里的数字 contractedId。
    /// 关键字搜索只剩一份合同时用那一份；多份里没有对上的编号时不猜。
    /// </summary>
    public static TikTokBusinessContract? MatchBusinessContract(JsonElement root, string configured)
    {
        var wanted = configured.Trim();
        TikTokBusinessContract? single = null;
        TikTokBusinessContract? exact = null;
        var exactCount = 0;
        var count = 0;
        foreach (var item in EnumerateContractItems(root))
        {
            var contract = ReadBusinessContract(item);
            if (contract is null)
                continue;
            count++;
            single = contract;
            if (string.Equals(contract.Value.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
                ContainsConfiguredCode(item, wanted))
            {
                exactCount++;
                exact = contract;
            }
        }

        if (exactCount == 1)
            return exact;
        return count == 1 ? single : null;
    }

    private static IEnumerable<JsonElement> EnumerateContractItems(JsonElement root)
    {
        foreach (var list in ContractLists(root))
        {
            foreach (var item in list.EnumerateArray())
                yield return item;
        }
    }

    private static bool ContainsConfiguredCode(JsonElement item, string wanted)
    {
        if (wanted.Length < 4 || item.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in item.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;
            var text = property.Value.GetString()?.Trim() ?? "";
            if (text.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static JsonObject Overlay(JsonObject? collection, TikTokApiSeriesShell shell)
    {
        var contract = collection?.DeepClone().AsObject() ?? new JsonObject();
        contract["title"] = shell.Title?.Trim() ?? "";
        contract["description"] = shell.Description?.Trim() ?? "";
        contract["totalVideoNum"] = shell.TotalVideoNum;
        contract["anchorPromotionStatus"] = shell.AnchorPromotionEnabled ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(shell.CoverUri))
            contract["coverUri"] = shell.CoverUri.Trim();
        if (!string.IsNullOrWhiteSpace(shell.BusinessContractId))
        {
            var businessContractId = shell.BusinessContractId.Trim();
            contract["contractedId"] = businessContractId;
            contract["contract"] = new JsonObject
            {
                ["id"] = businessContractId,
                ["contractId"] = businessContractId,
                ["title"] = shell.Title?.Trim() ?? "",
            };
        }

        var config = ObjectOf(contract, "config");
        if (!string.IsNullOrWhiteSpace(shell.SourceLanguage) &&
            !shell.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            config["sourceLanguage"] = shell.SourceLanguage.Trim();
        }

        var sync = ObjectOf(config, "contractSyncTag");
        sync["isAiSeries"] = shell.IsAiSeries;

        var tags = ObjectOf(config, "tags");
        if (shell.ThemeTagIds.Count > 0)
        {
            var themes = new JsonArray();
            foreach (var id in shell.ThemeTagIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    themes.Add(JsonValue.Create(id.Trim()));
            }
            tags["ShortDramaTheme"] = themes;
        }

        if (!string.IsNullOrWhiteSpace(shell.AudienceTagId))
        {
            tags["ShortDramaTargetAudience"] = new JsonArray(JsonValue.Create(shell.AudienceTagId.Trim()));
        }

        if (shell.ConsignmentEnabled)
        {
            contract["consignmentStatus"] = 1;
            contract["previewVideoNum"] = shell.ProfilePreviewEpisodes;
            var paid = new JsonObject
            {
                ["businessMode"] = 3,
                ["previewVideoNum"] = shell.FreePreviewEpisodes,
            };
            if (!string.IsNullOrWhiteSpace(shell.PriceInUsd))
            {
                paid["price"] = new JsonObject
                {
                    ["priceInUsd"] = shell.PriceInUsd.Trim(),
                    ["enableSingleVideoPurchase"] = false,
                };
            }

            config["consignmentList"] = new JsonArray(
                new JsonObject
                {
                    ["businessMode"] = 1,
                    ["previewVideoNum"] = shell.ProfilePreviewEpisodes,
                },
                paid);
        }

        if (shell.CopyrightFiles is not null)
        {
            var proof = ObjectOf(config, "copyrightProof");
            proof["isOriginalRightsHolder"] = shell.IsOriginalRightsHolder;
            proof["isAdaptation"] = shell.IsAdaptation;
            proof["commitmentSigned"] = true;
            proof["materials"] = BuildMaterials(shell.CopyrightFiles);
        }

        return contract;
    }

    public static string? ReadPrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var filtered = new string(text.Where(ch => char.IsDigit(ch) || ch is '.' or ',').ToArray())
            .Replace(',', '.');
        return decimal.TryParse(filtered, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price > 0
            ? price.ToString("0.##", CultureInfo.InvariantCulture)
            : null;
    }

    private static JsonArray BuildMaterials(IReadOnlyList<TikTokApiCopyrightFile> files)
    {
        var groups = new JsonArray();
        foreach (var type in files.Select(file => file.MaterialType).Distinct())
        {
            var items = new JsonArray();
            foreach (var file in files.Where(file => file.MaterialType == type))
            {
                items.Add(new JsonObject
                {
                    ["uri"] = file.Uri,
                    ["fileName"] = file.FileName,
                });
            }

            groups.Add(new JsonObject
            {
                ["materialType"] = type,
                ["files"] = items,
            });
        }

        return groups;
    }

    private static JsonObject ObjectOf(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }

                if (TryFindProperty(property.Value, name, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, name, out value))
                    return true;
            }
        }

        value = default;
        return false;
    }

    private static string? MatchTag(JsonElement group, IReadOnlyCollection<string> labels)
    {
        if (group.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in group.EnumerateArray())
            {
                var id = MatchOne(item, labels);
                if (id is not null)
                    return id;
            }
        }
        else if (group.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in group.EnumerateObject())
            {
                var id = MatchTag(property.Value, labels);
                if (id is not null)
                    return id;
            }
        }

        return null;
    }

    private static string? MatchOne(JsonElement item, IReadOnlyCollection<string> labels)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        string? label = null;
        foreach (var name in new[] { "name", "tagName", "label", "text" })
        {
            if (item.TryGetProperty(name, out var labelElement) && labelElement.ValueKind == JsonValueKind.String)
            {
                label = labelElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(label))
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(label) ||
            !labels.Any(candidate => label.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        foreach (var name in new[] { "id", "tagId", "value" })
        {
            if (!item.TryGetProperty(name, out var idElement))
                continue;
            var id = idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()?.Trim() ?? ""
                : idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetRawText()
                    : "";
            if (id.Length > 0)
                return id;
        }

        return null;
    }
}
