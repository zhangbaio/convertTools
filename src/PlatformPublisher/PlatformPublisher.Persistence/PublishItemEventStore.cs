using System.Text.Json;

namespace PlatformPublisher.Persistence;

public sealed record PublishItemEvent(
    string EventId,
    string JobId,
    string AccountId,
    string ItemKey,
    string Status,
    string Message,
    DateTimeOffset OccurredAt,
    object? Payload = null);

public sealed class PublishItemEventStore
{
    private readonly PlatformDatabase _database;
    public PublishItemEventStore(PlatformDatabase database) => _database = database;

    public void Save(PublishItemEvent value)
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(_database);
        _database.WriteGate.Wait();
        try
        {
            using var connection=_database.Open();using var command=connection.CreateCommand();
            command.CommandText="""
                INSERT INTO publish_item_events(event_id,job_id,account_id,item_key,status,message,occurred_at,payload_json)
                VALUES($id,$job,$account,$item,$status,$message,$at,$json)
                ON CONFLICT(event_id) DO UPDATE SET status=excluded.status,message=excluded.message,
                occurred_at=excluded.occurred_at,payload_json=excluded.payload_json
                """;
            command.Parameters.AddWithValue("$id",value.EventId);command.Parameters.AddWithValue("$job",value.JobId);
            command.Parameters.AddWithValue("$account",value.AccountId);command.Parameters.AddWithValue("$item",value.ItemKey);
            command.Parameters.AddWithValue("$status",value.Status);command.Parameters.AddWithValue("$message",value.Message);
            command.Parameters.AddWithValue("$at",value.OccurredAt.ToString("O"));command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(value.Payload??new{}));command.ExecuteNonQuery();
        }
        finally{_database.WriteGate.Release();}
    }
}
