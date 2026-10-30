namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 与短剧助手快手分账个人版上传前的配置解析一致：
/// 下拉存的是代码值，页面和产物使用对应文案；制作形式 auto 按视频方向展开；
/// 播出时间 today 取当天；制作机构由昵称和快手号拼接。
/// </summary>
public static class KuaishouPersonalFormResolver
{
    public sealed record Resolved(
        string ContentTypeLabel,
        string ComicMethodLabel,
        string AuthorDeclarationLabel,
        string CopyrightProofTypeLabel,
        string ProductionForm,
        int ProductionYear,
        int AverageDurationMinutes,
        string ProductionCost,
        bool Finished,
        bool FullSceneDisplay,
        bool HasRecordNumber,
        bool SpecialTheme,
        string BroadcastPlatform,
        string BroadcastChannel,
        string BroadcastDate,
        string ProductionOrg,
        string CreditName,
        string Gender,
        IReadOnlyList<KuaishouPersonalActor> Actors,
        string SaleTypeLabel,
        int SaleTypeCode,
        int FreeEpisodeCount,
        int UnlockCount,
        string EpisodePrice,
        bool SmallAmountUnlock);

    public static Resolved Resolve(
        KuaishouPersonalConfig config,
        int videoWidth = 0,
        int videoHeight = 0,
        DateTime? today = null)
    {
        var nickname = (config.KuaishouNickname ?? string.Empty).Trim();
        var kuaishouId = First(config.AdvertiserId, config.KuaishouId);
        var creditName = First(config.Username, config.RealName);
        var gender = First(config.Gender, "男");
        if (gender is not ("男" or "女")) gender = "男";
        var year = Math.Max(2000, ParseInt(config.ProductionYear, DateTime.Now.Year));
        var duration = Math.Max(1, ParseInt(config.AverageEpisodeMinutes, 1));
        var cost = string.IsNullOrWhiteSpace(config.ProductionCost) ? "1.0" : config.ProductionCost.Trim();
        var mode = string.IsNullOrWhiteSpace(config.BroadcastTimeMode) ? "today" : config.BroadcastTimeMode.Trim();
        return new Resolved(
            KuaishouFormChoices.LabelOf(KuaishouFormChoices.ContentTypes, config.ContentType, "漫剧"),
            KuaishouFormChoices.LabelOf(KuaishouFormChoices.ComicMethods, config.ProductionMethod, "3"),
            KuaishouFormChoices.LabelOf(KuaishouFormChoices.AuthorDeclarations, config.AuthorDeclaration, "1"),
            KuaishouFormChoices.LabelOf(KuaishouFormChoices.CopyrightProofTypes, config.CopyrightProofType, "1"),
            ResolveProductionForm(config.ProductionForm, videoWidth, videoHeight),
            year,
            duration,
            cost,
            config.Finished,
            config.RevenueShareFullSceneDisplay,
            config.HasRecordNumber,
            IsTruthy(config.SpecialTheme),
            First(config.BroadcastPlatform, "快手"),
            First(config.BroadcastChannel, "小屏APP"),
            ResolveBroadcastDate(mode, today ?? DateTime.Today),
            string.IsNullOrWhiteSpace(nickname) && string.IsNullOrWhiteSpace(kuaishouId) ? string.Empty : $"{nickname}+{kuaishouId}",
            creditName,
            gender,
            ResolveUploadActors(null, config),
            KuaishouFormChoices.LabelOf(KuaishouFormChoices.SaleTypes, config.SaleType, "3"),
            KuaishouFormChoices.SaleTypeCode(config.SaleType),
            Math.Max(1, config.FreeEpisodeCount),
            Math.Max(1, config.UnlockEpisodeCount),
            string.IsNullOrWhiteSpace(config.EpisodePrice) ? "0.11" : config.EpisodePrice.Trim(),
            config.SmallAmountUnlock);
    }

    public static string ResolveProductionForm(string? configured, int width, int height)
    {
        var text = (configured ?? string.Empty).Trim();
        if (text is "横屏" or "竖屏") return text;
        if (width > 0 && height > 0) return width >= height ? "横屏" : "竖屏";
        return "竖屏";
    }

    public static string ResolveBroadcastDate(string? mode, DateTime today)
    {
        var raw = (mode ?? string.Empty).Trim();
        if (raw.Length == 0 || raw.Equals("today", StringComparison.OrdinalIgnoreCase))
            return today.ToString("yyyy-MM-dd");
        if (DateTime.TryParse(raw, out var parsed)) return parsed.ToString("yyyy-MM-dd");
        return today.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// 上传时优先用项目补齐结果里的演员。姓名要来自演员库，角色要换成简介里的人物，
    /// 不能停在「演员A / 男主」这种占位。
    /// </summary>
    public static IReadOnlyList<KuaishouPersonalActor> ResolveUploadActors(string? workflowDirectory, KuaishouPersonalConfig config)
    {
        var prepared = ReadPreparedActors(workflowDirectory);
        if (prepared.Count > 0) return prepared;

        var characters = ReadPreparedCharacters(workflowDirectory);
        var selected = SelectLibraryActors(config, characters);
        return selected.Count > 0 ? selected : PlaceholderActors();
    }

    public static IReadOnlyList<KuaishouPersonalActor> ResolveActors(string? actorInfoJson)
    {
        var actors = new List<KuaishouPersonalActor>();
        if (!string.IsNullOrWhiteSpace(actorInfoJson))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(actorInfoJson);
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        var name = Text(item, "name", "姓名");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        actors.Add(new KuaishouPersonalActor(
                            name,
                            First(Text(item, "gender", "性别"), "男"),
                            First(Text(item, "role", "角色"), "主演")));
                        if (actors.Count == 2) break;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                actors.Clear();
            }
        }
        if (actors.Count > 0) return actors;
        return PlaceholderActors();
    }

    private static IReadOnlyList<KuaishouPersonalActor> PlaceholderActors() =>
    [
        new("演员A", "男", "男主"),
        new("演员B", "女", "女主"),
    ];

    private static IReadOnlyList<KuaishouPersonalActor> ReadPreparedActors(string? workflowDirectory)
    {
        if (!TryReadAutoFill(workflowDirectory, out var root) ||
            !root.TryGetProperty("actor_info_list", out var actors) ||
            actors.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        var prepared = new List<KuaishouPersonalActor>();
        foreach (var item in actors.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var name = Text(item, "name", "姓名");
            if (string.IsNullOrWhiteSpace(name) || IsPlaceholderName(name)) continue;
            var gender = First(Text(item, "gender", "性别"), "男");
            if (gender is not ("男" or "女")) gender = "男";
            prepared.Add(new KuaishouPersonalActor(name, gender, First(Text(item, "role", "角色"), "主演")));
            if (prepared.Count == 2) break;
        }
        return prepared;
    }

    private static IReadOnlyList<(string Name, string Gender)> ReadPreparedCharacters(string? workflowDirectory)
    {
        if (!TryReadAutoFill(workflowDirectory, out var root) ||
            !root.TryGetProperty("character_info_list", out var characters) ||
            characters.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        var prepared = new List<(string Name, string Gender)>();
        foreach (var item in characters.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var name = Text(item, "name", "姓名");
            if (string.IsNullOrWhiteSpace(name) || IsPlaceholderName(name)) continue;
            var gender = Text(item, "gender", "性别");
            prepared.Add((name, gender is "男" or "女" ? gender : string.Empty));
            if (prepared.Count == 2) break;
        }
        return prepared;
    }

    private static bool TryReadAutoFill(string? workflowDirectory, out System.Text.Json.JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(workflowDirectory)) return false;
        var path = Path.Combine(workflowDirectory, "kuaishou-auto-fill.json");
        if (!File.Exists(path)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            root = document.RootElement.Clone();
            return root.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<KuaishouPersonalActor> SelectLibraryActors(
        KuaishouPersonalConfig config,
        IReadOnlyList<(string Name, string Gender)> characters)
    {
        var pool = new List<KuaishouPersonalActor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in ResolveActors(config.ActorInfoJson).Where(actor => !IsPlaceholderName(actor.Name)))
            AddActor(pool, seen, actor);
        foreach (var actor in ParseActorLibrary(config.ActorLibraryText))
            AddActor(pool, seen, actor);

        var selected = new List<KuaishouPersonalActor>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var character in characters.Take(2))
        {
            var match = pool.FirstOrDefault(actor =>
                !used.Contains(actor.Name) &&
                (character.Gender is not ("男" or "女") || actor.Gender == character.Gender));
            if (match is null) continue;
            used.Add(match.Name);
            selected.Add(match with { Role = character.Name });
        }

        if (selected.Count == 0)
        {
            foreach (var gender in new[] { "男", "女" })
            {
                var match = pool.FirstOrDefault(actor => !used.Contains(actor.Name) && actor.Gender == gender);
                if (match is null) continue;
                used.Add(match.Name);
                selected.Add(match);
            }
        }
        return selected;
    }

    private static void AddActor(List<KuaishouPersonalActor> pool, HashSet<string> seen, KuaishouPersonalActor actor)
    {
        if (string.IsNullOrWhiteSpace(actor.Name) || !seen.Add(actor.Name)) return;
        pool.Add(actor);
    }

    private static IEnumerable<KuaishouPersonalActor> ParseActorLibrary(string? rawText)
    {
        foreach (var rawLine in (rawText ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Trim().Replace('｜', '|').Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0])) continue;
            var gender = parts[1] is "女" ? "女" : "男";
            var role = parts.Length >= 3
                ? parts[2].Split(['，', ',', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                : null;
            yield return new KuaishouPersonalActor(parts[0], gender, string.IsNullOrWhiteSpace(role) ? "主演" : role);
        }
    }

    private static bool IsPlaceholderName(string name) =>
        name.Trim() is "演员A" or "演员B" or "导演A" or "编剧A";

    public static Dictionary<string, object?> SettingsOverlay(KuaishouPersonalConfig config, bool enterprise)
    {
        var comic = KuaishouFormChoices.CanonicalValue(KuaishouFormChoices.ComicMethods, config.ProductionMethod, "3");
        var author = KuaishouFormChoices.CanonicalValue(KuaishouFormChoices.AuthorDeclarations, config.AuthorDeclaration, "1");
        var copyright = KuaishouFormChoices.CanonicalValue(KuaishouFormChoices.CopyrightProofTypes, config.CopyrightProofType, "1");
        var sale = KuaishouFormChoices.SaleTypeCode(config.SaleType);
        var prefix = enterprise ? "kuaishou_enterprise_" : "kuaishou_personal_";
        return new Dictionary<string, object?>
        {
            ["kuaishou_content_type"] = KuaishouFormChoices.CanonicalValue(KuaishouFormChoices.ContentTypes, config.ContentType, "漫剧"),
            ["kuaishou_comic_production_method"] = int.TryParse(comic, out var comicCode) ? comicCode : 3,
            ["kuaishou_author_declaration"] = int.TryParse(author, out var authorCode) ? authorCode : 1,
            ["kuaishou_production_form"] = config.ProductionForm,
            ["kuaishou_production_year"] = ParseInt(config.ProductionYear, DateTime.Now.Year),
            ["kuaishou_episode_average_duration_minutes"] = Math.Max(1, ParseInt(config.AverageEpisodeMinutes, 1)),
            ["kuaishou_production_cost"] = double.TryParse(config.ProductionCost, out var cost) ? cost : 1.0,
            ["kuaishou_sale_type"] = sale,
            ["kuaishou_free_episode_count"] = Math.Max(1, config.FreeEpisodeCount),
            ["kuaishou_unlock_count"] = Math.Max(1, config.UnlockEpisodeCount),
            ["kuaishou_small_amount_unlock"] = config.SmallAmountUnlock,
            ["kuaishou_episode_price_yuan"] = double.TryParse(config.EpisodePrice, out var episodePrice) ? episodePrice : 0.11,
            ["kuaishou_series_price_yuan"] = double.TryParse(config.SeriesPrice, out var seriesPrice) ? seriesPrice : 1.0,
            ["kuaishou_has_copyright_proof"] = config.HasCopyrightProof,
            ["kuaishou_copyright_proof_type"] = int.TryParse(copyright, out var copyrightCode) ? copyrightCode : 1,
            ["kuaishou_is_finished"] = config.Finished,
            ["kuaishou_full_scene_display"] = config.FullSceneDisplay,
            ["kuaishou_revenue_share_full_scene_display"] = config.RevenueShareFullSceneDisplay,
            ["kuaishou_has_record_number"] = config.HasRecordNumber,
            ["kuaishou_special_theme"] = IsTruthy(config.SpecialTheme),
            ["kuaishou_broadcast_platform"] = config.BroadcastPlatform,
            ["kuaishou_broadcast_channel"] = config.BroadcastChannel,
            ["kuaishou_broadcast_time_mode"] = string.IsNullOrWhiteSpace(config.BroadcastTimeMode) ? "today" : config.BroadcastTimeMode,
            ["kuaishou_plot_list"] = config.PlotLabels,
            ["kuaishou_actor_info_json"] = config.ActorInfoJson,
            ["kuaishou_actor_library_text"] = config.ActorLibraryText,
            ["kuaishou_director_info_json"] = config.DirectorInfoJson,
            ["kuaishou_screenwriter_info_json"] = config.ScreenwriterInfoJson,
            ["kuaishou_new_title_alias_enabled"] = config.NewTitleAliasEnabled,
            ["kuaishou_new_title_alias_position"] = config.NewTitleAliasPosition,
            ["kuaishou_new_title_alias_value"] = config.NewTitleAliasValue,
            ["kuaishou_project_image_template_root"] = config.ProjectImageTemplateRoot,
            ["kuaishou_project_image_template_id"] = config.UploadProjectImageTemplateId,
            ["kuaishou_personal_project_image_template_id"] = config.PersonalProjectImageTemplateId,
            ["kuaishou_enterprise_project_image_template_id"] = config.EnterpriseProjectImageTemplateId,
            ["kuaishou_personal_nickname"] = config.KuaishouNickname,
            ["kuaishou_advertiser_id"] = First(config.AdvertiserId, config.KuaishouId),
            ["kuaishou_commitment_recipient_company_name"] = config.CommitmentRecipientCompanyName,
            ["kuaishou_commitment_seal_path"] = config.CommitmentSealPath,
            ["kuaishou_commitment_template_docx_path"] = config.CommitmentTemplateDocxPath,
            ["kuaishou_first_page_submit_action"] = config.FirstPageAction,
            ["kuaishou_final_submit_action"] = config.FinalAction,
            ["kuaishou_revenue_share_headless_enabled"] = config.RevenueShareHeadless,
            ["kuaishou_keep_browser_open_on_failure"] = config.KeepBrowserOpenOnFailure,
            ["kuaishou_login_timeout_seconds"] = config.LoginTimeoutSeconds,
            ["kuaishou_queue_max_parallel_projects"] = config.QueueMaxParallelProjects,
            [prefix + "username"] = First(config.Username, config.RealName),
            [prefix + "gender"] = config.Gender,
            [prefix + "nickname"] = config.KuaishouNickname,
            [prefix + "kuaishou_id"] = First(config.AdvertiserId, config.KuaishouId),
            [prefix + "upload_entry_url"] = config.EntryUrl,
            [prefix + "commitment_pdf_path"] = config.CommitmentPdfPath,
            [prefix + "project_image_template_id"] = enterprise ? config.EnterpriseProjectImageTemplateId : config.PersonalProjectImageTemplateId,
        };
    }

    private static bool IsTruthy(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "是";

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static string Text(System.Text.Json.JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return string.Empty;
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
