using System.Text.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 按比例决定每部剧上传时采用「付费」还是「免费」方案（对齐 Python <c>paid_ratio_service.py</c>）。
/// 只影响 <see cref="Publishing.TikTokPublishOptions.PaidEnabled"/>，不改动上传流程本身。
/// </summary>
public static class TikTokPaidRatioService
{
    public const string StateSettingKey = "tiktok_paid_ratio_state";
    public const string PaidDecisionDocumentType = "paid_decision";
    private const string DecisionCacheFileName = "paid-decision.json";

    private static readonly object StateLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>测试注入：覆盖今日键（yyyy-MM-dd）。</summary>
    internal static Func<string>? TodayKeyOverride { get; set; }

    /// <summary>测试注入：覆盖主库路径。</summary>
    internal static Func<string>? DatabasePathOverride { get; set; }

    /// <summary>测试注入：覆盖 legacy JSON 状态路径。</summary>
    internal static Func<string>? LegacyStatePathOverride { get; set; }

    public static bool DecidePaidForUpload(
        TikTokAccountProfile account,
        string? workflowProjectDir = null,
        Action<string>? log = null,
        string? databasePath = null)
    {
        if (!account.TiktokPaidRatioEnabled)
            return account.TiktokPaidEnabled;

        var ratio = ResolveRatio(account);
        if (ratio <= 0.0)
            return false;
        if (ratio >= 1.0)
            return true;

        var workflowDir = NormalizeOptionalDir(workflowProjectDir);
        if (!string.IsNullOrWhiteSpace(workflowDir))
        {
            var cached = ReadDecision(workflowDir);
            if (cached is not null)
            {
                Emit(log, $"收费比例：沿用本剧已决策 = {(cached.Value ? "付费" : "免费")}（重传不重复计数）");
                return cached.Value;
            }
        }

        var today = TodayKey();
        var accountKey = AccountStateKey(account);
        bool paid;
        int paidCount;
        int total;
        lock (StateLock)
        {
            var state = LoadState(databasePath);
            var accountStates = StateAccounts(state);
            var accountState = StateForToday(accountStates.GetValueOrDefault(accountKey), today);
            var acc = AsDouble(accountState.Acc) + ratio;
            paid = acc >= 1.0;
            if (paid)
                acc -= 1.0;
            total = AsInt(accountState.Total) + 1;
            paidCount = AsInt(accountState.Paid) + (paid ? 1 : 0);
            accountStates[accountKey] = new PaidRatioAccountState
            {
                Date = today,
                Acc = acc,
                Total = total,
                Paid = paidCount,
            };
            SaveState(new PaidRatioRootState { Accounts = accountStates }, databasePath);
        }

        if (!string.IsNullOrWhiteSpace(workflowDir))
            WriteDecision(workflowDir, paid);

        var actual = total > 0 ? paidCount / (double)total * 100.0 : 0.0;
        Emit(
            log,
            $"收费比例：本剧 = {(paid ? "付费" : "免费")}"
            + $"（目标 {ratio * 100:0}% ，累计 {paidCount}/{total} ≈ {actual:0}%）");
        return paid;
    }

    private static double ResolveRatio(TikTokAccountProfile account)
    {
        var percent = account.TiktokPaidRatioPercent;
        if (double.IsNaN(percent) || double.IsInfinity(percent))
            percent = 0.0;
        return Math.Clamp(percent / 100.0, 0.0, 1.0);
    }

    private static string TodayKey() =>
        TodayKeyOverride?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");

    private static string ResolveDatabasePath(string? databasePath) =>
        databasePath
        ?? DatabasePathOverride?.Invoke()
        ?? AppPaths.AppDatabaseFile;

    private static string ResolveLegacyStatePath() =>
        LegacyStatePathOverride?.Invoke() ?? Path.Combine(AppPaths.DataRoot, "paid_ratio_state.json");

    private static string AccountStateKey(TikTokAccountProfile account)
    {
        var profileId = (account.Id ?? "").Trim();
        if (!string.IsNullOrEmpty(profileId))
            return profileId;

        var email = (account.TiktokLoginEmail ?? account.TiktokLastLoginEmail ?? "").Trim();
        return string.IsNullOrEmpty(email) ? "default" : email.ToLowerInvariant();
    }

    private static Dictionary<string, PaidRatioAccountState> StateAccounts(PaidRatioRootState? state)
    {
        if (state?.Accounts is { Count: > 0 } accounts)
        {
            return accounts.ToDictionary(
                pair => NormalizeAccountKey(pair.Key),
                pair => new PaidRatioAccountState
                {
                    Date = pair.Value.Date ?? "",
                    Acc = pair.Value.Acc,
                    Total = pair.Value.Total,
                    Paid = pair.Value.Paid,
                },
                StringComparer.Ordinal);
        }

        return new Dictionary<string, PaidRatioAccountState>(StringComparer.Ordinal);
    }

    private static PaidRatioAccountState StateForToday(PaidRatioAccountState? state, string today)
    {
        if (state is null || !string.Equals(state.Date, today, StringComparison.Ordinal))
            return new PaidRatioAccountState { Date = today };
        return state;
    }

    private static string NormalizeAccountKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private static double AsDouble(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;

    private static int AsInt(int value) => value;

    private static PaidRatioRootState LoadState(string? databasePath)
    {
        var path = ResolveDatabasePath(databasePath);
        if (AppSettingStore.TryLoadJson<PaidRatioRootState>(StateSettingKey, out var databaseState, path)
            && databaseState is not null)
        {
            return databaseState;
        }

        var legacyPath = ResolveLegacyStatePath();
        try
        {
            if (!File.Exists(legacyPath))
                return new PaidRatioRootState();

            var json = File.ReadAllText(legacyPath);
            var legacy = JsonSerializer.Deserialize<PaidRatioLegacyState>(json, JsonOptions);
            if (legacy is null)
                return new PaidRatioRootState();

            PaidRatioRootState migrated;
            if (legacy.Accounts is { Count: > 0 })
            {
                migrated = new PaidRatioRootState { Accounts = legacy.Accounts };
            }
            else
            {
                migrated = new PaidRatioRootState
                {
                    Accounts = new Dictionary<string, PaidRatioAccountState>(StringComparer.Ordinal)
                    {
                        [NormalizeAccountKey(legacy.Account)] = new PaidRatioAccountState
                        {
                            Date = legacy.Date ?? "",
                            Acc = legacy.Acc,
                            Total = legacy.Total,
                            Paid = legacy.Paid,
                        },
                    },
                };
            }

            SaveState(migrated, path);
            return migrated;
        }
        catch
        {
            return new PaidRatioRootState();
        }
    }

    private static void SaveState(PaidRatioRootState state, string? databasePath)
    {
        var path = ResolveDatabasePath(databasePath);
        try
        {
            AppSettingStore.SaveJson(StateSettingKey, state, path);
        }
        catch
        {
            try
            {
                var legacyPath = ResolveLegacyStatePath();
                Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
                File.WriteAllText(legacyPath, JsonSerializer.Serialize(state, JsonOptions));
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool? ReadDecision(string workflowProjectDir)
    {
        var databaseValue = LoadDecisionFromDatabase(workflowProjectDir);
        if (databaseValue is not null)
            return databaseValue;

        var cachePath = Path.Combine(workflowProjectDir, DecisionCacheFileName);
        try
        {
            if (!File.Exists(cachePath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
            if (!doc.RootElement.TryGetProperty("paid", out var paidElement)
                || paidElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var paid = paidElement.GetBoolean();
            SaveDecisionToDatabase(workflowProjectDir, paid);
            return paid;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteDecision(string workflowProjectDir, bool paid)
    {
        if (!SaveDecisionToDatabase(workflowProjectDir, paid))
        {
            try
            {
                var cachePath = Path.Combine(workflowProjectDir, DecisionCacheFileName);
                Directory.CreateDirectory(workflowProjectDir);
                File.WriteAllText(cachePath, JsonSerializer.Serialize(new { paid }));
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool? LoadDecisionFromDatabase(string workflowProjectDir)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(workflowProjectDir);
            var payload = ProjectStateDocumentStore.LoadDocument(
                context.WorkspaceRoot,
                context.SourceProjectDir,
                PaidDecisionDocumentType);
            if (!payload.TryGetValue("paid", out var paidElement))
                return null;
            return paidElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool SaveDecisionToDatabase(string workflowProjectDir, bool paid)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(workflowProjectDir);
            ProjectStateDocumentStore.SaveDocument(
                context.WorkspaceRoot,
                context.SourceProjectDir,
                PaidDecisionDocumentType,
                new Dictionary<string, object?> { ["paid"] = paid },
                context.WorkflowProjectDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeOptionalDir(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path.Trim());

    private static void Emit(Action<string>? log, string message) => log?.Invoke(message);

    private sealed class PaidRatioRootState
    {
        public Dictionary<string, PaidRatioAccountState> Accounts { get; set; } =
            new(StringComparer.Ordinal);
    }

    private sealed class PaidRatioAccountState
    {
        public string? Date { get; set; }
        public double Acc { get; set; }
        public int Total { get; set; }
        public int Paid { get; set; }
    }

    private sealed class PaidRatioLegacyState
    {
        public string? Account { get; set; }
        public string? Date { get; set; }
        public double Acc { get; set; }
        public int Total { get; set; }
        public int Paid { get; set; }
        public Dictionary<string, PaidRatioAccountState>? Accounts { get; set; }
    }
}
