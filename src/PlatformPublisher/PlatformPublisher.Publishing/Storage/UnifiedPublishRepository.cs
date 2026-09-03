using System.Text.Json;
using Microsoft.Data.Sqlite;
using PlatformPublisher.Persistence;
using PlatformPublisher.Publishing.Distribution;
using PlatformPublisher.Publishing.Execution;
using PlatformPublisher.Publishing.Models;

namespace PlatformPublisher.Publishing.Storage;

public sealed class UnifiedPublishRepository : IPublishBatchStore
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
    private readonly PlatformDatabase _database;
    public UnifiedPublishRepository(PlatformDatabase database){_database=database;PlatformDatabaseInitializer.EnsureMainDatabase(database);}

    public void SaveDraft(PublishDraft draft)
    {
        _database.WriteGate.Wait();try{using var connection=_database.Open();using var transaction=connection.BeginTransaction();
            using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="""
                INSERT INTO publish_drafts VALUES($id,$kind,$label,$workflow,$original,$new,$form,$media,$source,$created,$updated)
                ON CONFLICT(draft_id) DO UPDATE SET source_kind=excluded.source_kind,source_label=excluded.source_label,
                workflow_directory=excluded.workflow_directory,original_title=excluded.original_title,new_title=excluded.new_title,
                form_json=excluded.form_json,media_profile_json=excluded.media_profile_json,source_json=excluded.source_json,updated_at=excluded.updated_at
                """;command.Parameters.AddWithValue("$id",draft.Id);command.Parameters.AddWithValue("$kind",(int)draft.Source.Kind);command.Parameters.AddWithValue("$label",draft.Source.Label);command.Parameters.AddWithValue("$workflow",draft.Source.WorkflowDirectory);command.Parameters.AddWithValue("$original",draft.Source.OriginalTitle);command.Parameters.AddWithValue("$new",draft.Source.NewTitle);command.Parameters.AddWithValue("$form",Json(draft.Form));command.Parameters.AddWithValue("$media",Json(draft.MediaProcessing));command.Parameters.AddWithValue("$source",Json(draft.Source));command.Parameters.AddWithValue("$created",draft.CreatedAt.ToString("O"));command.Parameters.AddWithValue("$updated",draft.UpdatedAt.ToString("O"));command.ExecuteNonQuery();}
            using(var clear=connection.CreateCommand()){clear.Transaction=transaction;clear.CommandText="DELETE FROM material_items WHERE draft_id=$id";clear.Parameters.AddWithValue("$id",draft.Id);clear.ExecuteNonQuery();}
            foreach(var item in draft.Items){using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO material_items VALUES($draft,$id,$sequence,$video,$cover,$description,$short,$origin,$finalized)";command.Parameters.AddWithValue("$draft",draft.Id);command.Parameters.AddWithValue("$id",item.Id);command.Parameters.AddWithValue("$sequence",item.Sequence);command.Parameters.AddWithValue("$video",item.VideoPath);command.Parameters.AddWithValue("$cover",item.CoverPath??(object)DBNull.Value);command.Parameters.AddWithValue("$description",item.Description??(object)DBNull.Value);command.Parameters.AddWithValue("$short",item.ShortTitle??(object)DBNull.Value);command.Parameters.AddWithValue("$origin",Json(item.Origin));command.Parameters.AddWithValue("$finalized",item.ContentFinalized?1:0);command.ExecuteNonQuery();}
            transaction.Commit();
        }finally{_database.WriteGate.Release();}
    }

    public IReadOnlyList<PublishDraft> ListDrafts()
    {
        using var connection=_database.Open(readOnly:true);using var command=connection.CreateCommand();command.CommandText="SELECT draft_id,form_json,media_profile_json,source_json,created_at,updated_at FROM publish_drafts ORDER BY updated_at DESC";using var reader=command.ExecuteReader();var drafts=new List<PublishDraft>();
        while(reader.Read())drafts.Add(new PublishDraft{Id=reader.GetString(0),Form=From<UnifiedPublishForm>(reader.GetString(1)),MediaProcessing=From<MediaProcessingProfile>(reader.GetString(2)),Source=From<MaterialSourceSpec>(reader.GetString(3)),CreatedAt=DateTimeOffset.Parse(reader.GetString(4)),UpdatedAt=DateTimeOffset.Parse(reader.GetString(5))});reader.Close();foreach(var draft in drafts)draft.Items=LoadItems(connection,draft.Id);return drafts;
    }

    public void SaveStarted(PublishBatchRequest request,IReadOnlyList<AccountPublishPlan> plans,DateTimeOffset startedAt)
    {
        SaveDraft(request.Draft);_database.WriteGate.Wait();try{using var connection=_database.Open();using var transaction=connection.BeginTransaction();
            using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="INSERT INTO publish_batches VALUES($id,$draft,$retry,$distribution,$failure,$status,$form,$media,$started,NULL,'')";command.Parameters.AddWithValue("$id",request.BatchId);command.Parameters.AddWithValue("$draft",request.Draft.Id);command.Parameters.AddWithValue("$retry",request.RetryOfBatchId??(object)DBNull.Value);command.Parameters.AddWithValue("$distribution",(int)request.DistributionMode);command.Parameters.AddWithValue("$failure",(int)request.FailurePolicy);command.Parameters.AddWithValue("$status",(int)UnifiedPublishItemStatus.Running);command.Parameters.AddWithValue("$form",Json(request.Draft.Form));command.Parameters.AddWithValue("$media",Json(request.Draft.MediaProcessing));command.Parameters.AddWithValue("$started",startedAt.ToString("O"));command.ExecuteNonQuery();}
            foreach(var plan in plans){using(var account=connection.CreateCommand()){account.Transaction=transaction;account.CommandText="INSERT INTO publish_batch_accounts VALUES($batch,$account,$order,$name,$status,$total,0,'')";account.Parameters.AddWithValue("$batch",request.BatchId);account.Parameters.AddWithValue("$account",plan.Target.AccountId);account.Parameters.AddWithValue("$order",plan.Target.Order);account.Parameters.AddWithValue("$name",plan.Target.AccountName);account.Parameters.AddWithValue("$status",(int)UnifiedPublishItemStatus.Pending);account.Parameters.AddWithValue("$total",plan.Items.Count);account.ExecuteNonQuery();}foreach(var item in plan.Items){using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO publish_batch_items VALUES($batch,$account,$item,$order,$status,'',NULL,NULL,$source)";command.Parameters.AddWithValue("$batch",request.BatchId);command.Parameters.AddWithValue("$account",plan.Target.AccountId);command.Parameters.AddWithValue("$item",item.Id);command.Parameters.AddWithValue("$order",item.Sequence);command.Parameters.AddWithValue("$status",(int)UnifiedPublishItemStatus.Pending);command.Parameters.AddWithValue("$source",Json(item));command.ExecuteNonQuery();}}
            transaction.Commit();
        }finally{_database.WriteGate.Release();}
    }

    public void SaveAccountOutcome(string batchId,AccountPublishOutcome outcome)
    {
        _database.WriteGate.Wait();try{using var connection=_database.Open();using var transaction=connection.BeginTransaction();foreach(var item in outcome.Items){using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="UPDATE publish_batch_items SET status=$status,message=$message,started_at=$started,finished_at=$finished WHERE batch_id=$batch AND account_id=$account AND item_id=$item";command.Parameters.AddWithValue("$status",(int)item.Status);command.Parameters.AddWithValue("$message",item.Message);command.Parameters.AddWithValue("$started",item.StartedAt.ToString("O"));command.Parameters.AddWithValue("$finished",item.FinishedAt.ToString("O"));command.Parameters.AddWithValue("$batch",batchId);command.Parameters.AddWithValue("$account",outcome.AccountId);command.Parameters.AddWithValue("$item",item.ItemId);command.ExecuteNonQuery();}using var attempt=connection.CreateCommand();attempt.Transaction=transaction;attempt.CommandText="INSERT INTO publish_item_attempts VALUES($id,$batch,$account,$item,$number,$status,$error,$message,$started,$finished)";attempt.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));attempt.Parameters.AddWithValue("$batch",batchId);attempt.Parameters.AddWithValue("$account",outcome.AccountId);attempt.Parameters.AddWithValue("$item",item.ItemId);attempt.Parameters.AddWithValue("$number",item.Attempts);attempt.Parameters.AddWithValue("$status",(int)item.Status);attempt.Parameters.AddWithValue("$error",(int)item.ErrorKind);attempt.Parameters.AddWithValue("$message",item.Message);attempt.Parameters.AddWithValue("$started",item.StartedAt.ToString("O"));attempt.Parameters.AddWithValue("$finished",item.FinishedAt.ToString("O"));attempt.ExecuteNonQuery();}using(var account=connection.CreateCommand()){account.Transaction=transaction;account.CommandText="UPDATE publish_batch_accounts SET status=$status,completed_count=$completed,message=$message WHERE batch_id=$batch AND account_id=$account";account.Parameters.AddWithValue("$status",(int)outcome.Status);account.Parameters.AddWithValue("$completed",outcome.Items.Count(item=>item.Status is UnifiedPublishItemStatus.Success or UnifiedPublishItemStatus.DraftSaved));account.Parameters.AddWithValue("$message",outcome.Message);account.Parameters.AddWithValue("$batch",batchId);account.Parameters.AddWithValue("$account",outcome.AccountId);account.ExecuteNonQuery();}transaction.Commit();}finally{_database.WriteGate.Release();}
    }

    public void SaveFinished(PublishBatchOutcome outcome){_database.WriteGate.Wait();try{using var connection=_database.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE publish_batches SET status=$status,finished_at=$finished,message=$message WHERE batch_id=$id";command.Parameters.AddWithValue("$status",(int)outcome.Status);command.Parameters.AddWithValue("$finished",outcome.FinishedAt.ToString("O"));command.Parameters.AddWithValue("$message",outcome.Message);command.Parameters.AddWithValue("$id",outcome.BatchId);command.ExecuteNonQuery();}finally{_database.WriteGate.Release();}}

    public IReadOnlyList<(string BatchId,string DraftId,string Status,string Message,DateTimeOffset StartedAt,string? RetryOf)> ListHistory(){using var connection=_database.Open(readOnly:true);using var command=connection.CreateCommand();command.CommandText="SELECT batch_id,draft_id,status,message,started_at,retry_of_batch_id FROM publish_batches ORDER BY started_at DESC";using var reader=command.ExecuteReader();var result=new List<(string,string,string,string,DateTimeOffset,string?)>();while(reader.Read())result.Add((reader.GetString(0),reader.GetString(1),((UnifiedPublishItemStatus)reader.GetInt32(2)).ToString(),reader.GetString(3),DateTimeOffset.Parse(reader.GetString(4)),reader.IsDBNull(5)?null:reader.GetString(5)));return result;}

    public PublishBatchRequest CreateRetryRequest(string batchId,IReadOnlyList<PublishTarget> availableTargets)
    {
        using var connection=_database.Open(readOnly:true);string draftId;MaterialDistributionMode distribution;PublishFailurePolicy failure;
        using(var batch=connection.CreateCommand()){batch.CommandText="SELECT draft_id,distribution_mode,failure_policy FROM publish_batches WHERE batch_id=$id";batch.Parameters.AddWithValue("$id",batchId);using var reader=batch.ExecuteReader();if(!reader.Read())throw new InvalidOperationException("未找到要重试的发布批次。");draftId=reader.GetString(0);distribution=(MaterialDistributionMode)reader.GetInt32(1);failure=(PublishFailurePolicy)reader.GetInt32(2);}
        var draft=ListDrafts().FirstOrDefault(item=>item.Id==draftId)??throw new InvalidOperationException("发布草稿已不存在，无法重试。");
        var targetById=availableTargets.ToDictionary(item=>item.AccountId,StringComparer.OrdinalIgnoreCase);var assignments=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);var targets=new List<PublishTarget>();
        using var command=connection.CreateCommand();command.CommandText="""
            SELECT a.account_id,a.account_name,a.account_order,i.item_id,i.status
            FROM publish_batch_accounts a JOIN publish_batch_items i
              ON i.batch_id=a.batch_id AND i.account_id=a.account_id
            WHERE a.batch_id=$batch ORDER BY a.account_order,i.item_order
            """;command.Parameters.AddWithValue("$batch",batchId);using var rows=command.ExecuteReader();
        while(rows.Read())
        {
            var status=(UnifiedPublishItemStatus)rows.GetInt32(4);if(status is UnifiedPublishItemStatus.Success or UnifiedPublishItemStatus.DraftSaved)continue;
            var accountId=rows.GetString(0);if(!targetById.TryGetValue(accountId,out var target))throw new InvalidOperationException($"原批次账号“{rows.GetString(1)}”已不存在，无法保持原分配重试。");
            if(!assignments.TryGetValue(accountId,out var items)){assignments[accountId]=items=[];targets.Add(target);}items.Add(rows.GetString(3));
        }
        if(assignments.Count==0)throw new InvalidOperationException("该批次没有失败或未完成的素材。");
        return new PublishBatchRequest{Draft=draft,Targets=targets,DistributionMode=distribution,FailurePolicy=failure,RetryOfBatchId=batchId,FrozenAssignments=assignments};
    }
    private static List<ResolvedMaterial> LoadItems(SqliteConnection connection,string draftId){using var command=connection.CreateCommand();command.CommandText="SELECT item_id,sequence,video_path,cover_path,description,short_title,origin_json,content_finalized FROM material_items WHERE draft_id=$id ORDER BY sequence";command.Parameters.AddWithValue("$id",draftId);using var reader=command.ExecuteReader();var result=new List<ResolvedMaterial>();while(reader.Read())result.Add(new(){Id=reader.GetString(0),Sequence=reader.GetInt32(1),VideoPath=reader.GetString(2),CoverPath=reader.IsDBNull(3)?null:reader.GetString(3),Description=reader.IsDBNull(4)?null:reader.GetString(4),ShortTitle=reader.IsDBNull(5)?null:reader.GetString(5),Origin=From<MaterialOrigin>(reader.GetString(6)),ContentFinalized=reader.GetInt32(7)!=0});return result;}
    private static string Json<T>(T value)=>JsonSerializer.Serialize(value,JsonOptions);private static T From<T>(string json)=>JsonSerializer.Deserialize<T>(json,JsonOptions)??throw new InvalidOperationException($"无法读取{typeof(T).Name}。");
}
