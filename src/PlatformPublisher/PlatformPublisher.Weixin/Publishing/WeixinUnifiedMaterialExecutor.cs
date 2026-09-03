using System.Text.Json;
using System.Text.Json.Nodes;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Analytics.Services;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Publishing.Execution;
using PlatformPublisher.Publishing.Models;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinUnifiedMaterialExecutor : IUnifiedMaterialExecutor
{
    private readonly IWeixinChannelUploader _uploader;
    private readonly WeixinLocalVideoPublishService _localService;
    private readonly WeixinSystemHighlightPublishService _highlightService;
    private readonly AdxBatchStore _adxBatchStore;
    private readonly IAnalyticsActivitySink _analyticsSink;

    public WeixinUnifiedMaterialExecutor(IWeixinChannelUploader uploader,WeixinLocalVideoPublishService localService,
        WeixinSystemHighlightPublishService highlightService,AdxBatchStore adxBatchStore,IAnalyticsActivitySink analyticsSink)
    {
        _uploader=uploader;_localService=localService;_highlightService=highlightService;_adxBatchStore=adxBatchStore;_analyticsSink=analyticsSink;
    }

    public async Task<AccountPublishOutcome> ExecuteAccountAsync(string batchId,AccountPublishPlan plan,
        IProgress<UnifiedPublishProgress>? progress,CancellationToken cancellationToken)
    {
        if(plan.Items.All(item=>item.Origin.Kind==MaterialSourceKind.SystemHighlight))return await ExecuteSystemHighlightAsync(batchId,plan,progress,cancellationToken);
        var outcomes=new List<PublishItemOutcome>();
        foreach(var item in plan.Items.OrderBy(item=>item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();var outcome=await ExecuteWithRetryAsync(batchId,plan,item,progress,cancellationToken);outcomes.Add(outcome);
            progress?.Report(new(batchId,plan.Target.AccountId,item.Id,"completed",$"{plan.Target.AccountName}：已处理 {outcomes.Count}/{plan.Items.Count}",outcomes.Count,plan.Items.Count));
            if(outcome.ErrorKind is PublishErrorKind.AccountFatal or PublishErrorKind.SubmissionUnknown)break;
            if(plan.Form.StopOnError&&outcome.Status==UnifiedPublishItemStatus.Failed)break;
        }
        var status=Summarize(outcomes,plan.Form.FinalAction);var message=$"账号{plan.Target.AccountName}：完成{outcomes.Count(item=>item.Status is UnifiedPublishItemStatus.Success or UnifiedPublishItemStatus.DraftSaved)}/{plan.Items.Count}。";
        return new AccountPublishOutcome(plan.Target.AccountId,status,message,outcomes);
    }

    private async Task<PublishItemOutcome> ExecuteWithRetryAsync(string batchId,AccountPublishPlan plan,ResolvedMaterial item,IProgress<UnifiedPublishProgress>? progress,CancellationToken cancellationToken)
    {
        var started=DateTimeOffset.UtcNow;string message="";var errorKind=PublishErrorKind.None;var status=UnifiedPublishItemStatus.Failed;var attempts=0;
        for(var attempt=1;attempt<=PublishRetryPolicy.Delays.Length;attempt++)
        {
            attempts=attempt;var delay=PublishRetryPolicy.DelayBeforeAttempt(attempt);if(delay>TimeSpan.Zero)await Task.Delay(delay,cancellationToken);
            progress?.Report(new(batchId,plan.Target.AccountId,item.Id,"publish",$"{plan.Target.AccountName}：发布{item.Sequence}/{plan.Items.Count}，尝试{attempt}",Completed:0,Total:plan.Items.Count));
            try
            {
                var result=await ExecuteLocalItemAsync(batchId,plan,item,cancellationToken);status=result.Status;message=result.Message;errorKind=result.ErrorKind;
            }
            catch(OperationCanceledException){throw;}
            catch(Exception ex){message=ex.Message;errorKind=Classify(ex.Message);status=errorKind==PublishErrorKind.SubmissionUnknown?UnifiedPublishItemStatus.SubmissionUnknown:UnifiedPublishItemStatus.Failed;}
            if(status is UnifiedPublishItemStatus.Success or UnifiedPublishItemStatus.DraftSaved)break;
            if(!PublishRetryPolicy.CanRetry(errorKind,attempt))break;
        }
        var finished=DateTimeOffset.UtcNow;RecordOrigin(batchId,plan.Target,item,status,message,finished);return new PublishItemOutcome(item.Id,status,message,errorKind,started,finished,attempts);
    }

    private async Task<(UnifiedPublishItemStatus Status,string Message,PublishErrorKind ErrorKind)> ExecuteLocalItemAsync(string batchId,AccountPublishPlan plan,ResolvedMaterial item,CancellationToken cancellationToken)
    {
        if(!File.Exists(item.VideoPath))return(UnifiedPublishItemStatus.Failed,"视频文件不存在："+item.VideoPath,PublishErrorKind.Recoverable);
        var projectDirectory=Directory.Exists(plan.Source.WorkflowDirectory)?plan.Source.WorkflowDirectory:Path.GetDirectoryName(item.VideoPath)!;
        var job=new PublishJob{Id=$"{batchId}-{plan.Target.AccountId}-{item.Id}",Platform=PublishPlatform.WeixinChannel,Kind=PublishJobKind.CustomVideos,ProjectName=plan.Form.NewTitle,ProjectDirectory=projectDirectory,ConfigPath=plan.Target.ConfigPath,AccountId=plan.Target.AccountId,AccountName=plan.Target.AccountName,AccountSessionDirectory=plan.Target.SessionDirectory,PublishCount=1,CustomVideoFiles=[item.VideoPath],PlatformOptionsJson=MapOptions(plan.Form).ToJson()};
        var publishPlan=_localService.Prepare(job);PatchConfig(publishPlan.ConfigPath,plan,item);
        WeixinMaterialPublishItemResult? callback=null;
        var request=new WeixinUploadRequest(job.Id,job.ProjectDirectory,job.ProjectName,publishPlan.ConfigPath,Path.GetFileName(publishPlan.ConfigPath)){MaterialItemCompleted=value=>callback=value};
        var result=await _uploader.UploadAsync(request,null,cancellationToken);
        if(callback is not null)
        {
            var callbackStatus=callback.Status switch{"success"=>UnifiedPublishItemStatus.Success,"draft_saved"=>UnifiedPublishItemStatus.DraftSaved,"cancelled"=>UnifiedPublishItemStatus.Cancelled,_=>UnifiedPublishItemStatus.Failed};
            return(callbackStatus,callback.Message,callbackStatus is UnifiedPublishItemStatus.Success or UnifiedPublishItemStatus.DraftSaved?PublishErrorKind.None:Classify(callback.Message));
        }
        if(result.Ok)return(plan.Form.FinalAction==UnifiedFinalAction.Draft?UnifiedPublishItemStatus.DraftSaved:UnifiedPublishItemStatus.Success,result.Message??"完成",PublishErrorKind.None);
        var message=result.Message??"视频号发布失败。";return(Classify(message)==PublishErrorKind.SubmissionUnknown?UnifiedPublishItemStatus.SubmissionUnknown:UnifiedPublishItemStatus.Failed,message,Classify(message));
    }

    private async Task<AccountPublishOutcome> ExecuteSystemHighlightAsync(string batchId,AccountPublishPlan plan,IProgress<UnifiedPublishProgress>? progress,CancellationToken cancellationToken)
    {
        progress?.Report(new(batchId,plan.Target.AccountId,null,"publish",$"{plan.Target.AccountName}：生成并发布系统高光",0,plan.Items.Count));
        var payload=plan.Items[0].Origin.PayloadJson;var types="混剪,解说,切片";try{types=JsonNode.Parse(payload)?["videoTypes"]?.GetValue<string>()??types;}catch{ }
        var job=new PublishJob{Id=$"{batchId}-{plan.Target.AccountId}-highlight",Platform=PublishPlatform.WeixinChannel,Kind=PublishJobKind.SystemHighlight,ProjectName=plan.Form.NewTitle,ProjectDirectory=Directory.Exists(plan.Source.WorkflowDirectory)?plan.Source.WorkflowDirectory:Path.GetTempPath(),ConfigPath=plan.Target.ConfigPath,AccountId=plan.Target.AccountId,AccountName=plan.Target.AccountName,DramaTitle=plan.Form.NewTitle,PublishCount=plan.Items.Count,PublishVideoTypes=types,PlatformOptionsJson=MapOptions(plan.Form).ToJson()};
        var started=DateTimeOffset.UtcNow;try{await _highlightService.PublishAsync(job,null,cancellationToken);var status=plan.Form.FinalAction==UnifiedFinalAction.Draft?UnifiedPublishItemStatus.DraftSaved:UnifiedPublishItemStatus.Success;var items=plan.Items.Select(item=>new PublishItemOutcome(item.Id,status,"完成",PublishErrorKind.None,started,DateTimeOffset.UtcNow,1)).ToArray();progress?.Report(new(batchId,plan.Target.AccountId,null,"completed",$"{plan.Target.AccountName}：系统高光完成 {items.Length} 条",items.Length,items.Length));return new(plan.Target.AccountId,status,$"系统高光完成{items.Length}条。",items);}catch(Exception ex){var kind=Classify(ex.Message);var status=kind==PublishErrorKind.SubmissionUnknown?UnifiedPublishItemStatus.SubmissionUnknown:UnifiedPublishItemStatus.Failed;return new(plan.Target.AccountId,status,ex.Message,plan.Items.Select(item=>new PublishItemOutcome(item.Id,status,ex.Message,kind,started,DateTimeOffset.UtcNow,1)).ToArray());}
    }

    private void RecordOrigin(string batchId,PublishTarget target,ResolvedMaterial item,UnifiedPublishItemStatus status,string message,DateTimeOffset at)
    {
        var stored=status switch{UnifiedPublishItemStatus.Success=>"success",UnifiedPublishItemStatus.DraftSaved=>"draft_saved",UnifiedPublishItemStatus.Cancelled=>"cancelled",_=>"failed"};
        if(item.Origin.Kind==MaterialSourceKind.AdxBatch&&!string.IsNullOrWhiteSpace(item.Origin.ManifestPath))_adxBatchStore.RecordItem(item.Origin.ManifestPath,target.AccountId,item.Id,stored,message);
        var job=new PublishJob{Id=$"unified-{batchId}-{target.AccountId}",Platform=PublishPlatform.WeixinChannel,AccountId=target.AccountId,AccountName=target.AccountName,ProjectName="统一一键发布"};_analyticsSink.Record(job,item.Id,stored,at);
    }

    private static WeixinPublishOptions MapOptions(UnifiedPublishForm form)=>new(){EpisodeSelectionMode="all",FillDescription=form.FillDescription,AiDescriptionEnabled=form.AiDescriptionEnabled,DescriptionTemplate=form.DescriptionTemplate,FillShortTitle=form.FillShortTitle,ShortTitleMaxLength=form.ShortTitleMaxLength,DeclareOriginal=form.DeclareOriginal,LocationOptionText=form.LocationOption,LinkOptionText=form.LinkSeries?"视频号剧集":"",TimingOptionText=form.PlatformScheduledAt is null?"不定时":form.PlatformScheduledAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),ReplaceCoverWithLocalImage=form.CoverMode!="platform-default",CoverImagePath=form.CoverMode=="single-image"?form.CoverImagePath:"",FinalAction=form.FinalAction==UnifiedFinalAction.Draft?"draft":"publish",PauseOnError=form.StopOnError};

    private static void PatchConfig(string path,AccountPublishPlan plan,ResolvedMaterial item)
    {
        var root=JsonNode.Parse(File.ReadAllText(path))?.AsObject()??throw new InvalidOperationException("发布配置生成失败。");var publish=root["video_publish"]?.AsObject()??throw new InvalidOperationException("发布配置缺少video_publish。");
        publish["publish_video_custom_files"]=new JsonArray(JsonValue.Create(item.VideoPath));publish["publish_video_description_map"]=new JsonObject{{item.VideoPath,item.Description??plan.Form.DescriptionTemplate},{Path.GetFileName(item.VideoPath),item.Description??plan.Form.DescriptionTemplate}};
        publish["cover_image_path"]=plan.Form.CoverMode=="single-image"?plan.Form.CoverImagePath:item.CoverPath??"";publish["replace_cover_with_local_image"]=plan.Form.CoverMode!="platform-default";
        var media=plan.MediaProcessing;publish["publish_originality_enabled"]=media.Enabled;publish["publish_originality_reuse_across_runs"]=media.VariantMode==MediaVariantMode.Shared;publish["publish_originality_zoom"]=media.ZoomCrop;publish["publish_originality_color"]=media.ColorAdjust;publish["publish_originality_speed"]=media.SpeedAdjust;publish["publish_originality_fade"]=media.Fade;publish["publish_originality_sticker_dir"]=media.StickerStrip?media.StickerDirectory:"";
        File.WriteAllText(path,root.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
    }

    private static PublishErrorKind Classify(string message)
    {
        if(System.Text.RegularExpressions.Regex.IsMatch(message,"已点击.*(?:未确认|未知)|提交结果未知|submission.?unknown",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return PublishErrorKind.SubmissionUnknown;
        if(System.Text.RegularExpressions.Regex.IsMatch(message,"登录|扫码|账号.*失效|浏览器.*(?:关闭|不可用)|unauthorized",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return PublishErrorKind.AccountFatal;
        if(System.Text.RegularExpressions.Regex.IsMatch(message,"取消|停止",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return PublishErrorKind.Cancelled;
        return PublishErrorKind.Recoverable;
    }
    private static UnifiedPublishItemStatus Summarize(IReadOnlyList<PublishItemOutcome> outcomes,UnifiedFinalAction action){if(outcomes.Any(item=>item.Status==UnifiedPublishItemStatus.SubmissionUnknown))return UnifiedPublishItemStatus.SubmissionUnknown;if(outcomes.Count>0&&outcomes.All(item=>item.Status is UnifiedPublishItemStatus.Success or UnifiedPublishItemStatus.DraftSaved))return action==UnifiedFinalAction.Draft?UnifiedPublishItemStatus.DraftSaved:UnifiedPublishItemStatus.Success;if(outcomes.All(item=>item.Status==UnifiedPublishItemStatus.Cancelled))return UnifiedPublishItemStatus.Cancelled;return UnifiedPublishItemStatus.Failed;}
}
