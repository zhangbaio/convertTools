using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokExecutionHistoryServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _databasePath;

    public TikTokExecutionHistoryServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tiktok-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _databasePath = Path.Combine(_tempRoot, "app.db");
        AppDatabaseInitializer.EnsureInitialized(_databasePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void PruneOldEvents_removes_expired_normal_events()
    {
        InsertEvent("old", "2026-07-01T23:59:59");
        InsertEvent("before-cutoff", "2026-07-02T11:59:59");
        InsertEvent("at-cutoff", "2026-07-02T12:00:00");
        InsertEvent("new", "2026-07-05T11:00:00");

        var deleted = TikTokExecutionHistoryService.PruneOldEvents(
            databasePath: _databasePath,
            now: new DateTime(2026, 7, 5, 12, 0, 0));

        deleted.Should().Be(2);
        ReadEventIds().Should().Equal("at-cutoff", "new");
    }

    [Fact]
    public void EnsureStorageOptimized_migrates_latest_snapshot_and_removes_redundant_progress()
    {
        InsertEvent("progress-1", "2026-07-05T10:00:00", ProjectPayload("queue_progress", "执行中", ""));
        InsertEvent("progress-2", "2026-07-05T10:01:00", ProjectPayload("queue_progress", "已完成", ""));
        InsertEvent("failed", "2026-07-05T10:02:00", ProjectPayload("queue_progress", "失败", "network error"));
        InsertEvent("finished", "2026-07-05T10:03:00", "{\"event_type\":\"run_finished\"}");

        TikTokExecutionHistoryService.EnsureStorageOptimized(_databasePath);

        ReadScalar("SELECT COUNT(*) FROM upload_project_snapshots").Should().Be(1);
        ReadScalar("SELECT COUNT(*) FROM upload_task_events").Should().Be(2);
        ReadEventIds().Should().Equal("failed", "finished");
        ReadScalar("SELECT COUNT(*) FROM app_migrations WHERE migration_key = 'upload-history-snapshots-v1'").Should().Be(1);
    }

    [Fact]
    public void PersistDeletionSnapshot_preserves_project_and_account_for_recovery()
    {
        var item = new TikTokPublisher.Core.Queue.QueueProjectItem
        {
            ProjectDir = @"E:\tiktok\archive\workflow\_诡异游戏里我反成大反派",
            OriginalTitle = "怪谈玩家，但画风不对",
            NewTitle = "诡异游戏里我反成大反派",
            EpisodeCount = 50,
            UploadCompletedAt = "2026-07-04T17:25:09",
        };
        var account = new TikTokPublisher.Core.Models.TikTokAccountProfile
        {
            Id = "account-1",
            Name = "1544722162@qq.com",
        };

        TikTokExecutionHistoryService.PersistDeletionSnapshot(
            @"E:\tiktok",
            item,
            account,
            _databasePath);

        var snapshot = TikTokExecutionHistoryService
            .LoadProjectSnapshots(_databasePath)
            .Should()
            .ContainSingle()
            .Subject;
        snapshot.Workspace.Should().Be(@"E:\tiktok");
        snapshot.Item.OriginalTitle.Should().Be("怪谈玩家，但画风不对");
        snapshot.Item.NewTitle.Should().Be("诡异游戏里我反成大反派");
        snapshot.Item.AccountProfileId.Should().Be("account-1");
        snapshot.Item.AccountProfileName.Should().Be("1544722162@qq.com");
        ReadScalar("SELECT COUNT(*) FROM upload_task_events").Should().Be(1);
    }

    private void InsertEvent(string eventId, string createdAt, string payloadJson = "{}")
    {
        using var conn = new SqliteConnection($"Data Source={_databasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO upload_task_events(event_id, payload_json, created_at)
            VALUES($event_id, $payload_json, $created_at)
            """;
        cmd.Parameters.AddWithValue("$event_id", eventId);
        cmd.Parameters.AddWithValue("$payload_json", payloadJson);
        cmd.Parameters.AddWithValue("$created_at", createdAt);
        cmd.ExecuteNonQuery();
    }

    private IReadOnlyList<string> ReadEventIds()
    {
        using var conn = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT event_id FROM upload_task_events ORDER BY created_at ASC";

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private long ReadScalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string ProjectPayload(string eventType, string status, string error) => $$"""
        {
          "event_type": "{{eventType}}",
          "status": "{{status}}",
          "timestamp": "2026-07-05T10:00:00",
          "workspace": "E:\\tiktok",
          "project_dir": "E:\\tiktok\\demo",
          "original_title": "demo",
          "account_profile_id": "account-1",
          "status_text": "{{status}}",
          "last_error": "{{error}}",
          "error": "{{error}}",
          "step_key": "upload_series",
          "step_states": { "upload_series": "{{status}}" }
        }
        """;
}
