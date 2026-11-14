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

        var body = new JsonObject { ["contract"] = contract };
        if (contractId.Length > 0)
            body["contractId"] = contractId;
        return body.ToJsonString();
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

    public static string? ReadFirstContractId(JsonElement root)
    {
        string? found = null;
        Walk(root, 0);
        return found;

        void Walk(JsonElement element, int depth)
        {
            if (found is not null || depth > 8)
                return;
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var name in new[] { "contractId", "id" })
                    {
                        if (!element.TryGetProperty(name, out var idElement))
                            continue;
                        var id = idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString()?.Trim() ?? ""
                            : idElement.ValueKind == JsonValueKind.Number
                                ? idElement.GetRawText()
                                : "";
                        if (id.Length is >= 8 and <= 24 && id.All(char.IsDigit))
                        {
                            found = id;
                            return;
                        }
                    }

                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value, depth + 1);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item, depth + 1);
                    break;
            }
        }
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
