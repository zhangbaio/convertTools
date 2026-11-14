using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Models;

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
    public string? LicenseId { get; init; }
    public string? PriceInUsd { get; init; }
    public bool IsOriginalRightsHolder { get; init; } = true;
    public bool IsAdaptation { get; init; }
    public IReadOnlyList<TikTokApiCopyrightFile>? CopyrightFiles { get; init; }
    public IReadOnlyList<string> PublisherIds { get; init; } = [];
    public bool Signed { get; init; }
    public int PublishMode { get; init; }
}

public readonly record struct TikTokApiCopyrightFile(int MaterialType, string Uri, string FileName);

public static class TikTokApiSeriesDocument
{
    public readonly record struct TikTokSeriesContractBinding(string? ContractedId, string? LicenseId);

    /// <summary>发布账号。表单提交用的是 accountInfo.uid，creatorType 12 不出现在可选列表里。</summary>
    public readonly record struct TikTokPublisherAccount(string UserId, string RegionCode, string Nickname, int CreatorType);

    public const int HostedCreatorType = 12;

    public static string BuildCreateBody(string title, string? businessContractId) =>
        BuildCreateBody(title, new TikTokSeriesContractBinding(businessContractId, null));

    /// <summary>
    /// 授权合同账号新建时只带剧名和 licenseId。CT 编号放进 contract.contractId 会被当成 0。
    /// 机构合同才把数字 contractedId 放进 contract.contractId。
    /// </summary>
    public static string BuildCreateBody(string title, TikTokSeriesContractBinding binding)
    {
        var contract = new JsonObject { ["title"] = title?.Trim() ?? "" };
        var licenseId = binding.LicenseId?.Trim() ?? "";
        if (licenseId.Length > 0)
        {
            return new JsonObject
            {
                ["contract"] = contract,
                ["licenseId"] = licenseId,
            }.ToJsonString();
        }

        var contractId = binding.ContractedId?.Trim() ?? "";
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
        if (TryFindProperty(root, groupName, out var group))
        {
            var nested = MatchTag(group, labels);
            if (nested is not null)
                return nested;
        }

        return MatchGroupedTags(root, groupName, labels);
    }

    private static string? MatchGroupedTags(JsonElement element, string groupName, IReadOnlyCollection<string> labels)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var id = MatchGroupedTag(item, groupName, labels);
                if (id is not null)
                    return id;
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            var id = MatchGroupedTags(property.Value, groupName, labels);
            if (id is not null)
                return id;
        }

        return null;
    }

    private static string? MatchGroupedTag(JsonElement item, string groupName, IReadOnlyCollection<string> labels)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("tag_group", out var group) ||
            group.ValueKind != JsonValueKind.String ||
            !string.Equals(group.GetString(), groupName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return MatchOne(item, labels);
    }

    /// <summary>机构合同的 contractedId，和剧集 ID 一样是 16 到 20 位数字。</summary>
    public static bool IsPlatformContractId(string? value)
    {
        var id = value?.Trim() ?? "";
        return id.Length is >= 16 and <= 20 && id.All(char.IsDigit);
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

    public const string LicenseListRequestBody =
        """{"statusList":[2,4],"pageIndex":0,"pageSize":1000,"categoryList":[10]}""";

    /// <summary>授权合同列表里的 licenseId，保留接口返回的写法，重复编号只留一份。</summary>
    public static IReadOnlyList<string> ListLicenseIds(JsonElement root)
    {
        var licenses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ReadLicenseIds(root))
        {
            if (seen.Add(id))
                licenses.Add(id);
        }

        return licenses;
    }

    /// <summary>
    /// 授权合同列表在 data.list[].licenseId，值带 CT 前缀。
    /// 账号里可以填完整编号，也可以只填 CT 后面的数字。多份里对不上时不猜。
    /// </summary>
    public static string? MatchLicenseId(JsonElement root, string configured)
    {
        var licenses = ListLicenseIds(root);
        var wanted = configured.Trim();
        if (wanted.Length == 0)
            return licenses.Count == 1 ? licenses[0] : null;

        var withPrefix = wanted.StartsWith("CT", StringComparison.OrdinalIgnoreCase)
            ? wanted
            : "CT" + wanted;
        var matches = licenses
            .Where(id => id.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                         id.Equals(withPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static IEnumerable<string> ReadLicenseIds(JsonElement root)
    {
        foreach (var list in LicenseLists(root))
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("licenseId", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idElement.GetString()?.Trim() ?? "";
                if (id.Length > 0)
                    yield return id;
            }
        }
    }

    private static IEnumerable<JsonElement> LicenseLists(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            yield return list;
        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("list", out var nested) &&
            nested.ValueKind == JsonValueKind.Array)
        {
            yield return nested;
        }
    }

    /// <summary>
    /// 机构合同列表里，CT 编号可能写在别的字符串字段上，要对到数字 contractedId。
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
        if (!string.IsNullOrWhiteSpace(shell.LicenseId))
        {
            contract["licenseId"] = shell.LicenseId.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(shell.BusinessContractId))
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

        if (shell.PublisherIds.Count > 0)
        {
            var publishers = new JsonArray();
            foreach (var id in shell.PublisherIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    publishers.Add(id.Trim());
            }

            if (publishers.Count > 0)
                contract["publishers"] = publishers;
        }

        if (shell.Signed)
            contract["signed"] = true;
        if (shell.PublishMode > 0)
        {
            config["publishMode"] = shell.PublishMode;
            if (shell.PublishMode != 3)
                config["publishScheduleTime"] = "0";
        }

        contract["paidExpireTime"] = "4102444800";
        if (shell.ConsignmentEnabled && string.IsNullOrWhiteSpace(shell.PriceInUsd))
            config["price"] = new JsonObject();

        return contract;
    }

    public static string DescribePublisher(TikTokPublisherAccount account)
    {
        var country = RegionDisplayName(account.RegionCode);
        var nickname = AccountLabel(account.Nickname);
        if (country.Length == 0)
            return nickname;
        if (nickname.Length == 0)
            return country;
        return country + " / " + nickname;
    }

    /// <summary>1 过审后自动发布，2 手动发布，3 定时发布。</summary>
    public static int ResolvePublishMode(string? mode) =>
        mode?.Trim() switch
        {
            "manual" => 2,
            "scheduled" => 3,
            _ => 1,
        };

    public static IReadOnlyList<TikTokPublisherAccount> ReadPublisherAccounts(JsonElement root)
    {
        foreach (var list in FindNamedArrays(root, "creatorList"))
        {
            var accounts = new List<TikTokPublisherAccount>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryFindDirectProperty(item, "accountInfo", out var info) ||
                    info.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var userId = ReadText(info, "uid");
                if (userId.Length == 0)
                    continue;

                accounts.Add(new TikTokPublisherAccount(
                    userId,
                    ReadText(info, "store_region").ToUpperInvariant(),
                    ReadText(info, "nickname"),
                    ReadInt(item, "creatorType")));
            }

            if (accounts.Count > 0)
                return accounts;
        }

        return [];
    }

    /// <summary>
    /// 指定发布账号时按国家名和昵称对应 uid。未指定时每个国家取一个可勾选账号，和页面全选同一国家只留一个账号一致。
    /// </summary>
    public static IReadOnlyList<string> SelectPublisherIds(
        IReadOnlyList<TikTokPublisherAccount> accounts,
        bool customSelection,
        IReadOnlyList<string> selectedKeys)
    {
        var selectable = accounts
            .Where(account => account.CreatorType != HostedCreatorType && account.UserId.Length > 0)
            .ToList();
        if (!customSelection)
        {
            var ids = new List<string>();
            var seenRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in selectable)
            {
                var region = account.RegionCode.Length == 0 ? account.UserId : account.RegionCode;
                if (seenRegions.Add(region))
                    ids.Add(account.UserId);
            }

            if (ids.Count == 0)
                throw new InvalidOperationException("接口上传：机构下没有可勾选的发布账号。");
            return ids;
        }

        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in selectedKeys)
        {
            var key = NormalizeAccountKey(raw);
            if (key.Length > 0 && seen.Add(key))
                wanted.Add(key);
        }

        if (wanted.Count == 0)
            throw new InvalidOperationException("接口上传：已启用指定发布账号，但没有勾选任何账号。");

        var chosen = new List<string>();
        var missing = new List<string>();
        var ambiguous = new List<string>();
        foreach (var key in wanted)
        {
            var parts = key.Split(TikTokPublishAccountCatalog.KeySeparator, 2);
            var country = parts[0];
            var nickname = parts[1];
            var matches = selectable
                .Where(account =>
                    AccountLabel(account.Nickname) == nickname &&
                    RegionMatches(account.RegionCode, country))
                .Select(account => account.UserId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (matches.Count == 1)
                chosen.Add(matches[0]);
            else if (matches.Count == 0)
                missing.Add(TikTokPublishAccountCatalog.DescribeKey(key));
            else
                ambiguous.Add(TikTokPublishAccountCatalog.DescribeKey(key));
        }

        if (missing.Count > 0 || ambiguous.Count > 0)
        {
            var detail = new List<string>();
            if (missing.Count > 0)
                detail.Add("没有找到 " + string.Join("、", missing));
            if (ambiguous.Count > 0)
                detail.Add("对应到多个账号 " + string.Join("、", ambiguous));
            throw new InvalidOperationException("接口上传：发布账号无法写入。" + string.Join("；", detail) + "。");
        }

        return chosen;
    }

    private static bool RegionMatches(string regionCode, string country)
    {
        if (regionCode.Length == 0 || country.Length == 0)
            return false;
        if (regionCode.Equals(country, StringComparison.OrdinalIgnoreCase))
            return true;
        if (RegionDisplayName(regionCode).Equals(country, StringComparison.Ordinal))
            return true;
        return RegionEnglishName(regionCode).Equals(country, StringComparison.OrdinalIgnoreCase);
    }

    private static string RegionDisplayName(string regionCode) =>
        TikTokRegionNames.Chinese.TryGetValue(regionCode, out var name) ? name : regionCode;

    private static string RegionEnglishName(string regionCode)
    {
        try
        {
            return new RegionInfo(regionCode).EnglishName;
        }
        catch (ArgumentException)
        {
            return regionCode;
        }
    }

    private static string NormalizeAccountKey(string? key)
    {
        var parts = (key ?? "").Split(TikTokPublishAccountCatalog.KeySeparator, 2);
        return parts.Length == 2 ? TikTokPublishAccountCatalog.BuildKey(parts[0], parts[1]) : "";
    }

    private static string AccountLabel(string? value) =>
        string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IEnumerable<JsonElement> FindNamedArrays(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    yield return property.Value;
                }

                foreach (var nested in FindNamedArrays(property.Value, name))
                    yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in FindNamedArrays(item, name))
                    yield return nested;
            }
        }
    }

    private static bool TryFindDirectProperty(JsonElement element, string name, out JsonElement value)
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
            }
        }

        value = default;
        return false;
    }

    private static string ReadText(JsonElement element, string name)
    {
        if (!TryFindDirectProperty(element, name, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : value.ToString().Trim();
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (!TryFindDirectProperty(element, name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    public static string? ReadPrice(string? text)
    {
        if (TryReadPrice(text, out var price))
            return price.ToString("0.##", CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>
    /// 用用户填写的美元价格选下拉项。先对全集价，再对「每集」单价；
    /// 同一单价有多项时，选最接近「集数 × 单价」的全集价。
    /// </summary>
    public static int MatchPriceOption(IReadOnlyList<string> optionTexts, string? configuredPrice, int episodeCount)
    {
        if (!TryReadPrice(configuredPrice, out var wanted))
            return -1;

        var options = optionTexts
            .Select((text, index) => (index, text, numbers: ReadPriceNumbers(text)))
            .ToList();
        var exact = options.Where(item => item.numbers.Count > 0 && SamePrice(item.numbers[0], wanted)).ToList();
        if (exact.Count > 0)
            return exact[0].index;

        var unitHits = options
            .Select(item => (item.index, item.numbers, unit: ReadMarkedUnitPrice(item.text)))
            .Where(item => item.unit is { } unit && SamePrice(unit, wanted))
            .ToList();
        if (unitHits.Count == 0)
            return -1;
        if (episodeCount <= 0 || unitHits.Count == 1)
            return unitHits[0].index;

        var target = wanted * episodeCount;
        return unitHits
            .OrderBy(item => item.numbers.Count == 0 ? decimal.MaxValue : Math.Abs(item.numbers[0] - target))
            .ThenBy(item => item.index)
            .First()
            .index;
    }

    private static bool TryReadPrice(string? text, out decimal price)
    {
        price = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var filtered = new string(text.Where(ch => char.IsDigit(ch) || ch is '.' or ',').ToArray())
            .Replace(',', '.');
        return decimal.TryParse(filtered, NumberStyles.Number, CultureInfo.InvariantCulture, out price) && price > 0;
    }

    private static List<decimal> ReadPriceNumbers(string text)
    {
        var numbers = new List<decimal>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     text ?? "", @"\d+(?:\.\d+)?"))
        {
            if (decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                numbers.Add(number);
        }

        return numbers;
    }

    private static decimal? ReadMarkedUnitPrice(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text ?? "",
            @"每集\s*\$?\s*(\d+(?:\.\d+)?)|(\d+(?:\.\d+)?)\s*/\s*EP",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) ? price : null;
    }

    private static bool SamePrice(decimal left, decimal right) => Math.Abs(left - right) < 0.001m;

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
        foreach (var name in new[] { "name", "tagName", "tag_val", "label", "text" })
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
