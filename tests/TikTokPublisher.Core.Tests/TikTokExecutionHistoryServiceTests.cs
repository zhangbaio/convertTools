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
    public void PruneOldEvents_keeps_last_three_days_by_default()
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

    private void InsertEvent(string eventId, string createdAt)
    {
        using var conn = new SqliteConnection($"Data Source={_databasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO upload_task_events(event_id, payload_json, created_at)
            VALUES($event_id, $payload_json, $created_at)
            """;
        cmd.Parameters.AddWithValue("$event_id", eventId);
        cmd.Parameters.AddWithValue("$payload_json", "{}");
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
}
