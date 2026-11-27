using System.Text.Json;
using Microsoft.Data.Sqlite;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Common.Services;

public enum PublishJobScope
{
    All,
    Weixin,
    Kuaishou,
}

public sealed class PublishJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly PlatformDatabase _database;
    private readonly string _legacyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PublishJobStore(string? storePath = null)
    {
        _legacyPath = string.IsNullOrWhiteSpace(storePath) ? PlatformPublisherPaths.JobStorePath : Path.GetFullPath(storePath);
        var databasePath = string.IsNullOrWhiteSpace(storePath)
            ? PlatformPublisherPaths.MainDatabasePath
            : Path.GetExtension(_legacyPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(_legacyPath, ".db")
                : _legacyPath;
        _database = new PlatformDatabase(databasePath);
    }

    public PublishJobStore(PlatformDatabase database, string? legacyPath = null)
    {
        _database = database;
        _legacyPath = legacyPath ?? PlatformPublisherPaths.JobStorePath;
    }

    public string StorePath => _database.Path;

    public Task<IReadOnlyList<PublishJob>> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(PublishJobScope.All, cancellationToken);

    public async Task<IReadOnlyList<PublishJob>> LoadAsync(PublishJobScope scope, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PlatformDatabaseInitializer.EnsureMainDatabase(_database);
            RecoverInterruptedJobs(scope);
            var jobs = LoadDatabase(scope);
            if (jobs.Count > 0 || !File.Exists(_legacyPath) ||
                (scope != PublishJobScope.All && LoadDatabase(PublishJobScope.All).Count > 0)) return jobs;
            var legacy = await LoadLegacyAsync(cancellationToken);
            if (legacy.Count > 0) SaveDatabase(legacy, PublishJobScope.All);
            return legacy.Where(job => Includes(scope, job.Platform)).ToArray();
        }
        finally { _gate.Release(); }
    }

    public Task SaveAsync(IEnumerable<PublishJob> jobs, CancellationToken cancellationToken = default) =>
        SaveAsync(jobs, PublishJobScope.All, cancellationToken);

    public async Task SaveAsync(IEnumerable<PublishJob> jobs, PublishJobScope scope, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PlatformDatabaseInitializer.EnsureMainDatabase(_database);
            var snapshot = jobs.ToArray();
            if (snapshot.Any(job => !Includes(scope, job.Platform)))
                throw new ArgumentException("任务平台不属于当前队列。", nameof(jobs));
            SaveDatabase(snapshot, scope);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveCheckedAsync(string jobId, bool isChecked, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _database.WriteGate.WaitAsync(cancellationToken);
            try
            {
                using var connection = _database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE publish_jobs
                    SET payload_json = json_set(payload_json, '$.IsChecked', json($checked)),
                        row_version = row_version + 1
                    WHERE job_id = $id
                    """;
                command.Parameters.AddWithValue("$id", jobId);
                command.Parameters.AddWithValue("$checked", isChecked ? "true" : "false");
                command.ExecuteNonQuery();
            }
            finally { _database.WriteGate.Release(); }
        }
        finally { _gate.Release(); }
    }

    private List<PublishJob> LoadDatabase(PublishJobScope scope)
    {
        using var connection = _database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT job_id,platform,payload_json,status,updated_at FROM publish_jobs WHERE {PlatformCondition(scope)} ORDER BY created_at";
        using var reader = command.ExecuteReader();
        var jobs = new List<PublishJob>();
        while (reader.Read())
        {
            var job = Deserialize(reader.GetString(2));
            job.Id = reader.GetString(0);
            job.Platform = (PublishPlatform)reader.GetInt32(1);
            job.Status = (PublishJobStatus)reader.GetInt32(3);
            job.UpdatedAt = DateTimeOffset.Parse(reader.GetString(4));
            jobs.Add(job);
        }
        reader.Close();
        foreach (var job in jobs) job.StepStates = LoadSteps(connection, job.Id);
        return jobs;
    }

    private static Dictionary<string, PublishJobStepState> LoadSteps(SqliteConnection connection, string jobId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT step_key,status,label,message,started_at,completed_at,updated_at FROM publish_job_steps WHERE job_id=$id";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, PublishJobStepState>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var state = new PublishJobStepState
            {
                Key = reader.GetString(0), Status = (PublishJobStepStatus)reader.GetInt32(1),
                Label = reader.GetString(2), Message = reader.GetString(3),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(6)),
            };
            result[state.Key] = state;
        }
        return result;
    }

    private void SaveDatabase(IReadOnlyList<PublishJob> jobs, PublishJobScope scope)
    {
        _database.WriteGate.Wait();
        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            var ids = jobs.Select(job => job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var job in jobs)
            {
                SaveJob(connection, transaction, job, scope);
                using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM publish_job_steps WHERE job_id=$id";
                clear.Parameters.AddWithValue("$id", job.Id);
                clear.ExecuteNonQuery();
                foreach (var step in job.StepStates.Values) SaveStep(connection, transaction, job.Id, step);
            }
            DeleteMissing(connection, transaction, ids, scope);
            transaction.Commit();
        }
        finally { _database.WriteGate.Release(); }
    }

    private static void SaveJob(SqliteConnection connection, SqliteTransaction transaction, PublishJob job, PublishJobScope scope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO publish_jobs(job_id,platform,job_kind,account_id,project_name,project_directory,status,scheduled_at,attempt_count,payload_json,created_at,updated_at,row_version)
            VALUES($id,$p,$k,$a,$n,$d,$s,$schedule,$attempt,$json,$created,$updated,1)
            ON CONFLICT(job_id) DO UPDATE SET platform=excluded.platform,job_kind=excluded.job_kind,
            account_id=excluded.account_id,project_name=excluded.project_name,project_directory=excluded.project_directory,
            status=excluded.status,scheduled_at=excluded.scheduled_at,attempt_count=excluded.attempt_count,
            payload_json=excluded.payload_json,updated_at=excluded.updated_at,row_version=publish_jobs.row_version+1
            WHERE $allPlatforms OR publish_jobs.platform=excluded.platform
            """;
        command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$p", (int)job.Platform);
        command.Parameters.AddWithValue("$k", (int)job.Kind); command.Parameters.AddWithValue("$a", job.AccountId);
        command.Parameters.AddWithValue("$n", job.ProjectName); command.Parameters.AddWithValue("$d", job.ProjectDirectory);
        command.Parameters.AddWithValue("$s", (int)job.Status); command.Parameters.AddWithValue("$schedule", job.ScheduledAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$attempt", job.AttemptCount); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(job, JsonOptions));
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O")); command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$allPlatforms", scope == PublishJobScope.All);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException($"任务 {job.Id} 已属于其他平台。");
    }

    private static void SaveStep(SqliteConnection connection, SqliteTransaction transaction, string jobId, PublishJobStepState step)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO publish_job_steps VALUES($job,$key,$status,$label,$message,$started,$completed,$updated)";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$key", step.Key);
        command.Parameters.AddWithValue("$status", (int)step.Status); command.Parameters.AddWithValue("$label", step.Label);
        command.Parameters.AddWithValue("$message", step.Message); command.Parameters.AddWithValue("$started", DBNull.Value);
        command.Parameters.AddWithValue("$completed", DBNull.Value);
        command.Parameters.AddWithValue("$updated", step.UpdatedAt.ToString("O")); command.ExecuteNonQuery();
    }

    private static void DeleteMissing(SqliteConnection connection, SqliteTransaction transaction, HashSet<string> ids, PublishJobScope scope)
    {
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = $"SELECT job_id FROM publish_jobs WHERE {PlatformCondition(scope)}";
        var existing = new List<string>(); using var reader = select.ExecuteReader(); while (reader.Read()) existing.Add(reader.GetString(0)); reader.Close();
        foreach (var id in existing.Where(id => !ids.Contains(id)))
        {
            using var delete = connection.CreateCommand(); delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM publish_jobs WHERE job_id=$id"; delete.Parameters.AddWithValue("$id", id); delete.ExecuteNonQuery();
        }
    }

    private void RecoverInterruptedJobs(PublishJobScope scope)
    {
        _database.WriteGate.Wait();
        try
        {
            using var connection = _database.Open(); using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE publish_jobs SET status=$pending,updated_at=$at WHERE status=$running AND {PlatformCondition(scope)}";
            command.Parameters.AddWithValue("$pending", (int)PublishJobStatus.Pending);
            command.Parameters.AddWithValue("$running", (int)PublishJobStatus.Running);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
        }
        finally { _database.WriteGate.Release(); }
    }

    private static string PlatformCondition(PublishJobScope scope) => scope switch
    {
        PublishJobScope.Weixin => $"platform = {(int)PublishPlatform.WeixinChannel}",
        PublishJobScope.Kuaishou => $"platform IN ({(int)PublishPlatform.KuaishouPersonalRevenue}, {(int)PublishPlatform.KuaishouEnterpriseRevenue})",
        _ => "1 = 1",
    };

    private static bool Includes(PublishJobScope scope, PublishPlatform platform) => scope switch
    {
        PublishJobScope.Weixin => platform == PublishPlatform.WeixinChannel,
        PublishJobScope.Kuaishou => platform is PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue,
        _ => true,
    };

    private async Task<List<PublishJob>> LoadLegacyAsync(CancellationToken cancellationToken)
    {
        try { await using var stream = File.OpenRead(_legacyPath); return await JsonSerializer.DeserializeAsync<List<PublishJob>>(stream, JsonOptions, cancellationToken) ?? []; }
        catch { return []; }
    }
    private static PublishJob Deserialize(string json) { try { return JsonSerializer.Deserialize<PublishJob>(json, JsonOptions) ?? new PublishJob(); } catch { return new PublishJob(); } }
}
