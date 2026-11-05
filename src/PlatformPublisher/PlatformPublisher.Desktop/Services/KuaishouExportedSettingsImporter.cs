using System.Globalization;
using System.Text.Json;
using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Services;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using ChannelsAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.Services;

public sealed record KuaishouExportAccountPreview(
    string ProfileId,
    string Name,
    bool IsNew,
    bool HasPersonalLogin,
    bool HasEnterpriseLogin,
    IReadOnlyList<string> MissingPaths);

public sealed record KuaishouExportPreview(
    string ExportMode,
    IReadOnlyList<KuaishouExportAccountPreview> Accounts,
    int SkippedProfiles,
    int PersonalUploads = 0,
    int EnterpriseUploads = 0,
    int UnmatchedUploads = 0);

public sealed record KuaishouExportApplyResult(
    int Created,
    int Updated,
    int AuthFiles,
    int Assets,
    IReadOnlyList<string> MissingPaths,
    IReadOnlyList<ChannelsAccount> Accounts,
    int ImportedUploads = 0,
    int MergedUploads = 0,
    int SkippedUploads = 0,
    int UnmatchedUploads = 0);

public sealed class KuaishouExportedSettingsImporter
{
    private readonly AccountStore _accounts;
    private readonly string _dataRoot;

    public KuaishouExportedSettingsImporter(AccountStore accounts, string? dataRoot = null)
    {
        _accounts = accounts;
        _dataRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(dataRoot) ? PlatformPublisherPaths.DataRoot : dataRoot);
    }

    public KuaishouExportPreview Preview(KuaishouSettingsExportPackage package)
    {
        _accounts.Load();
        var rows = new List<KuaishouExportAccountPreview>();
        var skipped = 0;
        foreach (var profile in package.Profiles)
        {
            if (!IsKuaishouProfile(package, profile))
            {
                skipped++;
                continue;
            }
            var id = KuaishouSettingsExportPackage.Text(profile, "id");
            var name = FirstNonEmpty(KuaishouSettingsExportPackage.Text(profile, "name"), id);
            var existing = FindAccount(id, name);
            rows.Add(new KuaishouExportAccountPreview(
                id,
                name,
                existing is null,
                package.FindAuth(id, personal: true) is not null,
                package.FindAuth(id, personal: false) is not null,
                MissingPaths(package, profile, existing)));
        }
        var personalUploads = CollectUploads(package, personal: true, useLiveAccounts: false, out var unmatchedPersonal);
        var enterpriseUploads = CollectUploads(package, personal: false, useLiveAccounts: false, out var unmatchedEnterprise);
        return new KuaishouExportPreview(
            package.ExportMode,
            rows,
            skipped,
            personalUploads.Count,
            enterpriseUploads.Count,
            unmatchedPersonal + unmatchedEnterprise);
    }

    public string Describe(KuaishouExportPreview preview)
    {
        if (preview.Accounts.Count == 0)
            return "导出文件里没有快手分账账号。";
        var lines = new List<string>
        {
            $"将导入 {preview.Accounts.Count} 个快手分账账号。已有账号按原账号更新，不会重复新建。",
        };
        if (string.Equals(preview.ExportMode, "sanitized", StringComparison.OrdinalIgnoreCase))
            lines.Add("这是脱敏导出，空的密码和 token 不会覆盖云帆里已有的凭据。");
        if (preview.SkippedProfiles > 0)
            lines.Add($"跳过 {preview.SkippedProfiles} 个没有快手分账配置的账号。");
        foreach (var account in preview.Accounts)
        {
            lines.Add($"{account.Name}（{(account.IsNew ? "新建" : "更新")}）个人登录态：{(account.HasPersonalLogin ? "有" : "无")}；企业登录态：{(account.HasEnterpriseLogin ? "有" : "无")}");
            foreach (var path in account.MissingPaths.Take(4))
                lines.Add("  缺少：" + path);
        }
        lines.Add("不导入任务队列和视频。新建账号如果同时有个人版和企业版，需要之后再选择版本。");
        if (preview.PersonalUploads == 0 && preview.EnterpriseUploads == 0 && preview.UnmatchedUploads == 0)
            lines.Add("包里没有可导入的上传成功记录。");
        else
            lines.Add($"将导入上传成功的剧：个人版 {preview.PersonalUploads} 部，企业版 {preview.EnterpriseUploads} 部。同一账号下的重复剧名会自动合并。无法归类 {preview.UnmatchedUploads} 部。");
        return string.Join(Environment.NewLine, lines);
    }

    public async Task<KuaishouExportApplyResult> ApplyAsync(
        KuaishouSettingsExportPackage package,
        CancellationToken cancellationToken = default)
    {
        _accounts.Load();
        var created = 0;
        var updated = 0;
        var authFiles = 0;
        var assets = 0;
        var missing = new List<string>();
        var changed = new List<ChannelsAccount>();
        foreach (var profile in package.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsKuaishouProfile(package, profile))
                continue;
            var id = KuaishouSettingsExportPackage.Text(profile, "id");
            var name = FirstNonEmpty(KuaishouSettingsExportPackage.Text(profile, "name"), id, "快手账号");
            var existing = FindAccount(id, name);
            var isNew = existing is null;
            var account = existing ?? _accounts.Add(name);
            if (isNew) created++;
            else updated++;
            account.Name = name;
            if (!string.IsNullOrWhiteSpace(id))
                account.LegacyProfileId = id;
            account.LegacySessionSourceRoot = "weixin-channel-tool-export";
            account.LegacySessionImportedAt = DateTimeOffset.Now;
            if (isNew)
                account.KuaishouAccountEdition = EditionForNewAccount(package, profile);

            var personalPath = ConfigPath(account.Id, personal: true);
            var enterprisePath = ConfigPath(account.Id, personal: false);
            var personal = LoadConfig(account, PublishPlatform.KuaishouPersonalRevenue, personalPath);
            var enterprise = LoadConfig(account, PublishPlatform.KuaishouEnterpriseRevenue, enterprisePath);
            var previousPersonalWorkspace = account.KuaishouPersonalWorkRootDirectory;
            var previousEnterpriseWorkspace = account.KuaishouEnterpriseWorkRootDirectory;
            var previousArchive = account.ArchiveRootDirectory;
            var previousEnterpriseArchive = enterprise.ArchiveRootDirectory;
            var previousWorkRoot = account.WorkRootDirectory;

            KuaishouExportedConfigMerger.Apply(package.Settings, profile, personal, PublishPlatform.KuaishouPersonalRevenue);
            KuaishouExportedConfigMerger.Apply(package.Settings, profile, enterprise, PublishPlatform.KuaishouEnterpriseRevenue);
            KuaishouExportedConfigMerger.ShareSecrets(personal, enterprise);

            ApplyDirectory(
                KuaishouSettingsExportPackage.Text(profile, "kuaishou_personal_last_workspace"),
                previousPersonalWorkspace,
                value => account.KuaishouPersonalWorkRootDirectory = value,
                value => personal.LastWorkspace = value,
                missing);
            ApplyDirectory(
                KuaishouSettingsExportPackage.Text(profile, "kuaishou_enterprise_last_workspace"),
                previousEnterpriseWorkspace,
                value => account.KuaishouEnterpriseWorkRootDirectory = value,
                value => enterprise.LastWorkspace = value,
                missing);
            ApplyDirectory(
                FirstNonEmpty(
                    KuaishouSettingsExportPackage.Text(profile, "kuaishou_personal_archive_root_dir"),
                    KuaishouSettingsExportPackage.Text(profile, "kuaishou_archive_root_dir")),
                previousArchive,
                value => account.ArchiveRootDirectory = value,
                value => personal.ArchiveRootDirectory = value,
                missing);
            var enterpriseArchive = KuaishouSettingsExportPackage.Text(profile, "kuaishou_enterprise_archive_root_dir");
            ApplyDirectory(enterpriseArchive, previousEnterpriseArchive, _ => { }, value => enterprise.ArchiveRootDirectory = value, missing);
            if (string.IsNullOrWhiteSpace(previousWorkRoot))
            {
                account.WorkRootDirectory = FirstExisting(
                    account.KuaishouPersonalWorkRootDirectory,
                    account.KuaishouEnterpriseWorkRootDirectory) ?? account.WorkRootDirectory;
            }

            var nickname = KuaishouSettingsExportPackage.Text(profile, "kuaishou_personal_nickname");
            if (!string.IsNullOrWhiteSpace(nickname))
                account.Nickname = nickname;
            var personalAccount = FirstNonEmpty(nickname, KuaishouSettingsExportPackage.Text(profile, "kuaishou_personal_username"));
            if (!string.IsNullOrWhiteSpace(personalAccount))
                account.KuaishouPersonalAccount = personalAccount;
            var enterpriseAccount = FirstNonEmpty(
                KuaishouSettingsExportPackage.Text(profile, "kuaishou_enterprise_nickname"),
                KuaishouSettingsExportPackage.Text(profile, "kuaishou_enterprise_username"));
            if (!string.IsNullOrWhiteSpace(enterpriseAccount))
                account.KuaishouEnterpriseAccount = enterpriseAccount;

            assets += RewriteAsset(package, id, personal.CommitmentPdfPath, value => personal.CommitmentPdfPath = value, missing);
            assets += RewriteAsset(package, id, personal.CommitmentSealPath, value => personal.CommitmentSealPath = value, missing);
            assets += RewriteAsset(package, id, personal.CommitmentTemplateDocxPath, value => personal.CommitmentTemplateDocxPath = value, missing);
            assets += RewriteAsset(package, id, enterprise.CommitmentPdfPath, value => enterprise.CommitmentPdfPath = value, missing);
            assets += RewriteAsset(package, id, enterprise.CommitmentSealPath, value => enterprise.CommitmentSealPath = value, missing);
            assets += RewriteAsset(package, id, enterprise.CommitmentTemplateDocxPath, value => enterprise.CommitmentTemplateDocxPath = value, missing);

            if (await CopyAuthAsync(package, account, personal, personal: true, cancellationToken))
                authFiles++;
            if (await CopyAuthAsync(package, account, enterprise, personal: false, cancellationToken))
                authFiles++;

            var enterpriseLast = !string.Equals(account.KuaishouAccountEdition, "enterprise", StringComparison.OrdinalIgnoreCase);
            if (enterpriseLast)
            {
                await enterprise.SaveAsync(enterprisePath, cancellationToken);
                await personal.SaveAsync(personalPath, cancellationToken);
            }
            else
            {
                await personal.SaveAsync(personalPath, cancellationToken);
                await enterprise.SaveAsync(enterprisePath, cancellationToken);
            }
            account.KuaishouPersonalConfigPath = personalPath;
            account.KuaishouEnterpriseConfigPath = enterprisePath;
            _accounts.Update(account);
            changed.Add(account);
        }

        KuaishouTodayUploadCounter.EnsureStore(_dataRoot);
        var personalUploads = CollectUploads(package, personal: true, useLiveAccounts: true, out var unmatchedPersonal);
        var enterpriseUploads = CollectUploads(package, personal: false, useLiveAccounts: true, out var unmatchedEnterprise);
        var imported = 0;
        var merged = 0;
        var skipped = 0;
        foreach (var upload in personalUploads.Concat(enterpriseUploads))
        {
            switch (KuaishouUploadRecordStore.ImportSuccess(upload, _dataRoot))
            {
                case KuaishouUploadImportResult.Inserted:
                    imported++;
                    break;
                case KuaishouUploadImportResult.Updated:
                    merged++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        return new KuaishouExportApplyResult(
            created,
            updated,
            authFiles,
            assets,
            missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            changed,
            imported,
            merged,
            skipped,
            unmatchedPersonal + unmatchedEnterprise);
    }

    private readonly record struct UploadOwner(string ProfileId, string Name, string PersonalNickname, string EnterpriseNickname);

    private List<KuaishouUploadRecord> CollectUploads(
        KuaishouSettingsExportPackage package,
        bool personal,
        bool useLiveAccounts,
        out int unmatched)
    {
        unmatched = 0;
        var bytes = package.FindUploadRecord(personal
            ? "kuaishou_personal_upload_records.json"
            : "kuaishou_enterprise_upload_records.json");
        if (bytes is null || bytes.Length == 0)
            return [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var owners = useLiveAccounts ? LiveOwners() : PackageOwners(package);
            var collapsed = new Dictionary<string, KuaishouUploadRecord>(StringComparer.OrdinalIgnoreCase);
            var unmatchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!string.Equals(KuaishouSettingsExportPackage.Text(item, "上传状态"), "成功", StringComparison.Ordinal))
                    continue;
                var profileId = KuaishouSettingsExportPackage.Text(item, "账号档案");
                var accountName = FirstNonEmpty(
                    KuaishouSettingsExportPackage.Text(item, "账号名称"),
                    OwnerName(owners, profileId));
                var nickname = FirstNonEmpty(
                    KuaishouSettingsExportPackage.Text(item, "快手昵称"),
                    OwnerNickname(owners, profileId, accountName, personal));
                var path = KuaishouSettingsExportPackage.Text(item, "项目路径");
                var projectKey = KuaishouUploadRecordStore.ProjectKey(path);
                if (string.IsNullOrWhiteSpace(projectKey))
                    projectKey = FirstNonEmpty(
                        KuaishouSettingsExportPackage.Text(item, "新剧名"),
                        KuaishouSettingsExportPackage.Text(item, "原剧名"),
                        KuaishouSettingsExportPackage.Text(item, "项目名称"));
                if (string.IsNullOrWhiteSpace(projectKey))
                    continue;
                var owner = MatchOwner(owners, profileId, accountName, nickname, personal);
                if (owner is null)
                {
                    unmatchedKeys.Add(projectKey);
                    continue;
                }

                var accountId = useLiveAccounts ? LiveAccountId(owner.Value) : owner.Value.ProfileId;
                if (string.IsNullOrWhiteSpace(accountId))
                {
                    unmatchedKeys.Add(projectKey);
                    continue;
                }

                int.TryParse(KuaishouSettingsExportPackage.Text(item, "剧集数"), out var episodes);
                if (episodes == 0)
                    int.TryParse(KuaishouSettingsExportPackage.Text(item, "集数"), out episodes);
                var record = new KuaishouUploadRecord(
                    personal ? "personal" : "enterprise",
                    accountId,
                    FirstNonEmpty(nickname, personal ? owner.Value.PersonalNickname : owner.Value.EnterpriseNickname),
                    ParseRecordedAt(FirstNonEmpty(
                        KuaishouSettingsExportPackage.Text(item, "记录时间"),
                        KuaishouSettingsExportPackage.Text(item, "日期"))),
                    "成功",
                    FirstNonEmpty(KuaishouSettingsExportPackage.Text(item, "项目名称"), KuaishouSettingsExportPackage.Text(item, "新剧名"), projectKey),
                    FirstNonEmpty(KuaishouSettingsExportPackage.Text(item, "原剧名"), projectKey),
                    FirstNonEmpty(KuaishouSettingsExportPackage.Text(item, "新剧名"), KuaishouSettingsExportPackage.Text(item, "项目名称"), projectKey),
                    KuaishouSettingsExportPackage.Text(item, "作者"),
                    episodes,
                    path,
                    "");
                var collapseKey = accountId + "|" + projectKey;
                if (!collapsed.TryGetValue(collapseKey, out var current) || record.RecordedAt >= current.RecordedAt)
                    collapsed[collapseKey] = MergeUpload(record, current);
                else
                    collapsed[collapseKey] = MergeUpload(current, record);
            }

            unmatched = unmatchedKeys.Count;
            return collapsed.Values.ToList();
        }
    }

    private static KuaishouUploadRecord MergeUpload(KuaishouUploadRecord newer, KuaishouUploadRecord? older)
    {
        if (older is null)
            return newer;
        return newer with
        {
            Nickname = FirstNonEmpty(newer.Nickname, older.Nickname),
            ProjectName = FirstNonEmpty(newer.ProjectName, older.ProjectName),
            OriginalTitle = FirstNonEmpty(newer.OriginalTitle, older.OriginalTitle),
            NewTitle = FirstNonEmpty(newer.NewTitle, older.NewTitle),
            Author = FirstNonEmpty(newer.Author, older.Author),
            EpisodeCount = newer.EpisodeCount > 0 ? newer.EpisodeCount : older.EpisodeCount,
            ProjectDirectory = FirstNonEmpty(newer.ProjectDirectory, older.ProjectDirectory),
        };
    }

    private List<UploadOwner> PackageOwners(KuaishouSettingsExportPackage package)
    {
        var owners = new List<UploadOwner>();
        foreach (var profile in package.Profiles)
        {
            if (!IsKuaishouProfile(package, profile))
                continue;
            owners.Add(new UploadOwner(
                KuaishouSettingsExportPackage.Text(profile, "id"),
                KuaishouSettingsExportPackage.Text(profile, "name"),
                KuaishouSettingsExportPackage.Text(profile, "kuaishou_personal_nickname"),
                FirstNonEmpty(
                    KuaishouSettingsExportPackage.Text(profile, "kuaishou_enterprise_nickname"),
                    KuaishouSettingsExportPackage.Text(profile, "kuaishou_personal_nickname"))));
        }
        return owners;
    }

    private List<UploadOwner> LiveOwners()
    {
        return _accounts.Accounts.Select(account => new UploadOwner(
            account.LegacyProfileId,
            account.Name,
            FirstNonEmpty(account.Nickname, account.KuaishouPersonalAccount),
            FirstNonEmpty(account.KuaishouEnterpriseAccount, account.Nickname))).ToList();
    }

    private string LiveAccountId(UploadOwner owner)
    {
        if (!string.IsNullOrWhiteSpace(owner.ProfileId))
        {
            var byId = _accounts.Accounts.FirstOrDefault(item =>
                string.Equals(item.LegacyProfileId, owner.ProfileId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId.Id;
        }

        if (string.IsNullOrWhiteSpace(owner.Name))
            return "";
        var byName = _accounts.Accounts.Where(item =>
            string.Equals(item.Name, owner.Name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0].Id : "";
    }

    private static UploadOwner? MatchOwner(
        IReadOnlyList<UploadOwner> owners,
        string profileId,
        string accountName,
        string nickname,
        bool personal)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var byId = owners.Where(owner => string.Equals(owner.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byId.Count == 1)
                return byId[0];
        }

        if (!string.IsNullOrWhiteSpace(nickname))
        {
            var byNickname = owners.Where(owner => string.Equals(personal ? owner.PersonalNickname : owner.EnterpriseNickname, nickname, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byNickname.Count == 1)
                return byNickname[0];
        }

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            var byName = owners.Where(owner => string.Equals(owner.Name, accountName, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (byName.Count == 1)
                return byName[0];
        }

        return null;
    }

    private static string OwnerName(IReadOnlyList<UploadOwner> owners, string profileId)
    {
        var owner = owners.FirstOrDefault(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        return owner.Name;
    }

    private static string OwnerNickname(IReadOnlyList<UploadOwner> owners, string profileId, string accountName, bool personal)
    {
        var owner = owners.FirstOrDefault(item =>
            string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(accountName) && string.Equals(item.Name, accountName, StringComparison.CurrentCultureIgnoreCase)));
        return personal ? owner.PersonalNickname : owner.EnterpriseNickname;
    }

    private static DateTimeOffset ParseRecordedAt(string raw)
    {
        if (DateTime.TryParseExact(raw, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Local));
        return DateTimeOffset.Now;
    }

    private async Task<bool> CopyAuthAsync(
        KuaishouSettingsExportPackage package,
        ChannelsAccount account,
        KuaishouPersonalConfig config,
        bool personal,
        CancellationToken cancellationToken)
    {
        var bytes = package.FindAuth(account.LegacyProfileId, personal);
        if (bytes is null)
            return false;
        try
        {
            LegacyAccountSessionImportService.ValidateStorageState(bytes, "kuaishou.com");
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        var destination = Path.Combine(_dataRoot, "sessions", Safe(account.Id),
            personal ? "kuaishou-personal-storage-state.json" : "kuaishou-enterprise-storage-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
        config.AuthStatePath = destination;
        if (personal) account.KuaishouPersonalAuthStatePath = destination;
        else account.KuaishouEnterpriseAuthStatePath = destination;
        return true;
    }

    private int RewriteAsset(
        KuaishouSettingsExportPackage package,
        string profileId,
        string? current,
        Action<string> assign,
        List<string> missing)
    {
        if (string.IsNullOrWhiteSpace(current) || File.Exists(current))
            return 0;
        if (!package.AssetsByOriginalPath.TryGetValue(current, out var asset))
        {
            missing.Add(current);
            return 0;
        }
        var destination = Path.Combine(_dataRoot, "imported-kuaishou-assets", Safe(profileId), Safe(asset.FileName));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, asset.Bytes);
        assign(destination);
        return 1;
    }

    private static void ApplyDirectory(
        string exported,
        string? current,
        Action<string> assignAccount,
        Action<string> assignConfig,
        List<string> missing)
    {
        if (string.IsNullOrWhiteSpace(exported))
            return;
        if (Directory.Exists(exported))
        {
            assignAccount(exported);
            assignConfig(exported);
            return;
        }
        missing.Add(exported);
        if (!string.IsNullOrWhiteSpace(current))
        {
            assignAccount(current);
            assignConfig(current);
        }
        else
        {
            assignAccount(exported);
            assignConfig(exported);
        }
    }

    private List<string> MissingPaths(KuaishouSettingsExportPackage package, JsonElement profile, ChannelsAccount? existing)
    {
        var missing = new List<string>();
        NoteMissingDirectory(KuaishouSettingsExportPackage.Text(profile, "kuaishou_personal_last_workspace"), existing?.KuaishouPersonalWorkRootDirectory, missing);
        NoteMissingDirectory(KuaishouSettingsExportPackage.Text(profile, "kuaishou_enterprise_last_workspace"), existing?.KuaishouEnterpriseWorkRootDirectory, missing);
        NoteMissingDirectory(KuaishouSettingsExportPackage.Text(profile, "kuaishou_personal_archive_root_dir"), existing?.ArchiveRootDirectory, missing);
        foreach (var key in new[]
        {
            "kuaishou_personal_commitment_pdf_path",
            "kuaishou_enterprise_commitment_pdf_path",
            "kuaishou_commitment_seal_path",
            "kuaishou_commitment_template_docx_path",
        })
        {
            var path = KuaishouSettingsExportPackage.Text(profile, key);
            if (string.IsNullOrWhiteSpace(path) || File.Exists(path) || package.AssetsByOriginalPath.ContainsKey(path))
                continue;
            missing.Add(path);
        }
        return missing;
    }

    private static void NoteMissingDirectory(string exported, string? current, List<string> missing)
    {
        if (!string.IsNullOrWhiteSpace(exported) && !Directory.Exists(exported))
            missing.Add(exported);
        _ = current;
    }

    private KuaishouPersonalConfig LoadConfig(ChannelsAccount account, PublishPlatform platform, string path)
    {
        if (string.Equals(Path.GetFullPath(_dataRoot), Path.GetFullPath(PlatformPublisherPaths.DataRoot), StringComparison.OrdinalIgnoreCase))
        {
            return KuaishouPersonalConfig.Load(new PublishJob
            {
                Platform = platform,
                AccountId = account.Id,
                ConfigPath = File.Exists(path) ? path : string.Empty,
            });
        }
        if (!File.Exists(path))
            return new KuaishouPersonalConfig();
        return JsonSerializer.Deserialize<KuaishouPersonalConfig>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new KuaishouPersonalConfig();
    }

    private string ConfigPath(string accountId, bool personal)
    {
        var edition = personal ? "kuaishou-personal" : "kuaishou-enterprise";
        var file = personal ? "kuaishou-personal-config.json" : "kuaishou-enterprise-config.json";
        return Path.Combine(_dataRoot, edition, "accounts", Safe(accountId), file);
    }

    private ChannelsAccount? FindAccount(string profileId, string name)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var byId = _accounts.Accounts.FirstOrDefault(account =>
                string.Equals(account.LegacyProfileId, profileId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return _accounts.Accounts.FirstOrDefault(account =>
            string.Equals(account.Name, name, StringComparison.CurrentCultureIgnoreCase) &&
            string.IsNullOrWhiteSpace(account.LegacyProfileId));
    }

    private static bool IsKuaishouProfile(KuaishouSettingsExportPackage package, JsonElement profile) =>
        package.HasEditionData(profile, personal: true) || package.HasEditionData(profile, personal: false);

    private static string EditionForNewAccount(KuaishouSettingsExportPackage package, JsonElement profile)
    {
        var personal = package.HasEditionData(profile, personal: true);
        var enterprise = package.HasEditionData(profile, personal: false);
        if (personal && !enterprise) return "personal";
        if (enterprise && !personal) return "enterprise";
        return "";
    }

    private static string? FirstExisting(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                return value;
        }
        return null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "").FirstOrDefault(value => value.Length > 0) ?? "";

    private static string Safe(string value) => string.Concat((value ?? "").Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
}
