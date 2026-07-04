using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TikTokPublisher.Core.Services;

public sealed record AiRewriteHistoryRecord(
    string OriginalTitle,
    string OriginalSynopsis,
    string NewTitle,
    string NewSynopsis,
    string ProjectName,
    string ProjectDir,
    string WorkspacePath,
    string AccountProfileId,
    string AccountProfileName,
    string VariantKey,
    string ModelName,
    string CreatedAt);

public static class AiRewriteHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static IReadOnlyList<AiRewriteHistoryRecord> LoadForOriginalTitle(string originalTitle, int limit = 5000)
    {
        var originalKey = NormalizeTitleKey(originalTitle);
        if (string.IsNullOrWhiteSpace(originalKey)) return [];

        var records = new List<AiRewriteHistoryRecord>();
        records.AddRange(LoadFromDatabase(ClientSettingsStore.MainDatabasePath, createIfMissing: true, limit));
        records.AddRange(LoadFromDatabase(AppPaths.LegacyUploaderDatabaseFile, createIfMissing: false, limit));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<AiRewriteHistoryRecord>();
        foreach (var record in records.Where(record => NormalizeTitleKey(record.OriginalTitle) == originalKey))
        {
            var key = $"{NormalizeTitleKey(record.OriginalTitle)}\n{NormalizeTitleKey(record.NewTitle)}\n{NormalizeSynopsisKey(record.NewSynopsis)}\n{record.VariantKey.Trim()}";
            if (!seen.Add(key)) continue;
            output.Add(record);
        }

        return output;
    }

    public static void Append(AiRewriteHistoryRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.OriginalTitle)) return;

        var path = ClientSettingsStore.MainDatabasePath;
        AppDatabaseInitializer.EnsureInitialized(path);
        SaveToDatabase(path, NormalizeRecord(record));
    }

    public static bool IsTitleDuplicate(string title, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TitlesEqual(title, candidate) || TitlesTooSimilar(title, candidate))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSynopsisDuplicate(string synopsis, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (SynopsesTooSimilar(synopsis, candidate))
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeTitleKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '《' or '》' or '“' or '”' or '"' or '\'' or '-' or '_' or '：' or ':' or '，' or ',' or '。' or '.')
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<AiRewriteHistoryRecord> LoadFromDatabase(string databasePath, bool createIfMissing, int limit)
    {
        try
        {
            var path = Path.GetFullPath(databasePath);
            if (createIfMissing)
            {
                AppDatabaseInitializer.EnsureInitialized(path);
            }
            else if (!File.Exists(path))
            {
                return [];
            }

            using var conn = new SqliteConnection(createIfMissing ? $"Data Source={path}" : $"Data Source={path};Mode=ReadOnly");
            conn.Open();
            if (!TableExists(conn, "ai_rewrite_history")) return [];

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    account_profile_id,
                    original_title,
                    original_synopsis,
                    new_title,
                    new_synopsis,
                    variant_key,
                    model_name,
                    created_at,
                    payload_json
                FROM ai_rewrite_history
                ORDER BY rowid ASC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));

            var records = new List<AiRewriteHistoryRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var payload = ParsePayload(ReadString(reader, 8));
                var record = new AiRewriteHistoryRecord(
                    FirstNonEmpty(Pick(payload, "original_title", "originalTitle"), ReadString(reader, 1)),
                    FirstNonEmpty(Pick(payload, "original_synopsis", "originalSynopsis"), ReadString(reader, 2)),
                    FirstNonEmpty(Pick(payload, "new_title", "newTitle"), ReadString(reader, 3)),
                    FirstNonEmpty(Pick(payload, "new_synopsis", "newSynopsis", "synopsis"), ReadString(reader, 4)),
                    Pick(payload, "project_name", "projectName"),
                    Pick(payload, "project_dir", "projectDir"),
                    Pick(payload, "workspace_path", "workspacePath", "workspace"),
                    FirstNonEmpty(Pick(payload, "account_profile_id", "accountProfileId"), ReadString(reader, 0)),
                    Pick(payload, "account_profile_name", "accountProfileName"),
                    FirstNonEmpty(Pick(payload, "variant_key", "variantKey"), ReadString(reader, 5)),
                    FirstNonEmpty(Pick(payload, "model_name", "modelName"), ReadString(reader, 6)),
                    FirstNonEmpty(Pick(payload, "created_at", "createdAt"), ReadString(reader, 7)));

                if (!string.IsNullOrWhiteSpace(record.OriginalTitle))
                {
                    records.Add(record);
                }
            }

            return records;
        }
        catch
        {
            return [];
        }
    }

    private static void SaveToDatabase(string databasePath, AiRewriteHistoryRecord record)
    {
        var payload = new Dictionary<string, object?>
        {
            ["rewrite_id"] = StableRewriteId(record),
            ["account_profile_id"] = record.AccountProfileId,
            ["account_profile_name"] = record.AccountProfileName,
            ["original_title"] = record.OriginalTitle,
            ["original_synopsis"] = record.OriginalSynopsis,
            ["new_title"] = record.NewTitle,
            ["new_synopsis"] = record.NewSynopsis,
            ["synopsis"] = record.NewSynopsis,
            ["project_name"] = record.ProjectName,
            ["project_dir"] = record.ProjectDir,
            ["workspace_path"] = record.WorkspacePath,
            ["variant_key"] = record.VariantKey,
            ["model_name"] = record.ModelName,
            ["created_at"] = record.CreatedAt,
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var conn = new SqliteConnection($"Data Source={databasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ai_rewrite_history (
                rewrite_id,
                account_profile_id,
                original_title,
                original_synopsis,
                new_title,
                new_synopsis,
                variant_key,
                model_name,
                created_at,
                payload_json
            )
            VALUES (
                $rewrite_id,
                $account_profile_id,
                $original_title,
                $original_synopsis,
                $new_title,
                $new_synopsis,
                $variant_key,
                $model_name,
                $created_at,
                $payload_json
            )
            ON CONFLICT(rewrite_id) DO UPDATE SET
                account_profile_id = excluded.account_profile_id,
                original_title = excluded.original_title,
                original_synopsis = excluded.original_synopsis,
                new_title = excluded.new_title,
                new_synopsis = excluded.new_synopsis,
                variant_key = excluded.variant_key,
                model_name = excluded.model_name,
                created_at = excluded.created_at,
                payload_json = excluded.payload_json
            """;
        cmd.Parameters.AddWithValue("$rewrite_id", payload["rewrite_id"]?.ToString() ?? Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$account_profile_id", record.AccountProfileId);
        cmd.Parameters.AddWithValue("$original_title", record.OriginalTitle);
        cmd.Parameters.AddWithValue("$original_synopsis", record.OriginalSynopsis);
        cmd.Parameters.AddWithValue("$new_title", record.NewTitle);
        cmd.Parameters.AddWithValue("$new_synopsis", record.NewSynopsis);
        cmd.Parameters.AddWithValue("$variant_key", record.VariantKey);
        cmd.Parameters.AddWithValue("$model_name", record.ModelName);
        cmd.Parameters.AddWithValue("$created_at", record.CreatedAt);
        cmd.Parameters.AddWithValue("$payload_json", json);
        cmd.ExecuteNonQuery();
    }

    private static AiRewriteHistoryRecord NormalizeRecord(AiRewriteHistoryRecord record)
    {
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        return record with
        {
            OriginalTitle = record.OriginalTitle.Trim(),
            OriginalSynopsis = record.OriginalSynopsis.Trim(),
            NewTitle = record.NewTitle.Trim(),
            NewSynopsis = record.NewSynopsis.Trim(),
            ProjectName = record.ProjectName.Trim(),
            ProjectDir = NormalizePath(record.ProjectDir),
            WorkspacePath = NormalizePath(record.WorkspacePath),
            AccountProfileId = record.AccountProfileId.Trim(),
            AccountProfileName = record.AccountProfileName.Trim(),
            VariantKey = record.VariantKey.Trim(),
            ModelName = record.ModelName.Trim(),
            CreatedAt = string.IsNullOrWhiteSpace(record.CreatedAt) ? now : record.CreatedAt.Trim(),
        };
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static Dictionary<string, string> ParsePayload(string json)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return payload;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return payload;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                payload[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.ToString();
            }
        }
        catch
        {
            // Ignore corrupt legacy payloads; columns still carry the important fields.
        }

        return payload;
    }

    private static string ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

    private static string Pick(IReadOnlyDictionary<string, string> payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static bool TitlesEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return NormalizeTitleKey(left) == NormalizeTitleKey(right);
    }

    private static bool TitlesTooSimilar(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        var normalizedLeft = NormalizeTitleKey(left);
        var normalizedRight = NormalizeTitleKey(right);
        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight)) return false;
        if (normalizedLeft == normalizedRight) return true;

        var commonPrefixLength = GetCommonPrefixLength(normalizedLeft, normalizedRight);
        var minLength = Math.Min(normalizedLeft.Length, normalizedRight.Length);
        if (minLength >= 5 &&
            commonPrefixLength >= 5 &&
            commonPrefixLength >= (int)Math.Floor(minLength * 0.7))
        {
            return true;
        }

        return normalizedLeft.StartsWith(normalizedRight, StringComparison.Ordinal) ||
               normalizedRight.StartsWith(normalizedLeft, StringComparison.Ordinal);
    }

    private static bool SynopsesTooSimilar(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        var normalizedLeft = NormalizeSynopsisKey(left);
        var normalizedRight = NormalizeSynopsisKey(right);
        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight)) return false;
        if (normalizedLeft == normalizedRight) return true;
        if (Math.Min(normalizedLeft.Length, normalizedRight.Length) < 24) return false;

        var lcs = LongestCommonSubsequenceLength(normalizedLeft, normalizedRight);
        var ratio = (double)lcs / Math.Max(normalizedLeft.Length, normalizedRight.Length);
        return ratio >= 0.86;
    }

    private static string NormalizeSynopsisKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch)) continue;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static int LongestCommonSubsequenceLength(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (right.Length > left.Length)
        {
            (left, right) = (right, left);
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = left[i - 1] == right[j - 1]
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[right.Length];
    }

    private static int GetCommonPrefixLength(string left, string right)
    {
        var max = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < max && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private static string StableRewriteId(AiRewriteHistoryRecord record)
    {
        var key = string.Join(
            "\n",
            NormalizeTitleKey(record.OriginalTitle),
            NormalizeTitleKey(record.NewTitle),
            NormalizeSynopsisKey(record.NewSynopsis),
            record.VariantKey.Trim());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim()); }
        catch { return path.Trim(); }
    }
}
