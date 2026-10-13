using PlatformPublisher.Persistence;
using PlatformPublisher.Publishing.Distribution;
using PlatformPublisher.Publishing.Models;
using PlatformPublisher.Publishing.Storage;
using Xunit;

namespace PlatformPublisher.Publishing.Tests;

public sealed class UnifiedPublishingTests
{
    [Fact]
    public void Balanced_distribution_is_complete_stable_and_without_duplicates()
    {
        var request=Request(7,3,MaterialDistributionMode.Balanced);var plans=MaterialDistributionPlanner.Build(request);
        Assert.Equal(new[]{3,2,2},plans.Select(item=>item.Items.Count).ToArray());
        Assert.Equal(7,plans.SelectMany(item=>item.Items).Select(item=>item.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1,7),plans.SelectMany(item=>item.Items).Select(item=>item.Sequence));
    }

    [Fact]
    public void Broadcast_assigns_all_materials_to_every_account()
    {
        var plans=MaterialDistributionPlanner.Build(Request(4,2,MaterialDistributionMode.Broadcast));
        Assert.All(plans,plan=>Assert.Equal(4,plan.Items.Count));
    }

    [Fact]
    public void Balanced_rejects_more_accounts_than_materials()=>
        Assert.Throws<InvalidOperationException>(()=>MaterialDistributionPlanner.Build(Request(1,2,MaterialDistributionMode.Balanced)));

    [Fact]
    public void Repository_roundtrip_and_retry_keep_only_failed_assignment()
    {
        var root=Path.Combine(Path.GetTempPath(),"unified-publish-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var repository=new UnifiedPublishRepository(new PlatformDatabase(Path.Combine(root,"test.db")));var request=Request(2,1,MaterialDistributionMode.Broadcast);var plans=MaterialDistributionPlanner.Build(request);repository.SaveStarted(request,plans,DateTimeOffset.UtcNow);
            var now=DateTimeOffset.UtcNow;repository.SaveAccountOutcome(request.BatchId,new AccountPublishOutcome("a1",UnifiedPublishItemStatus.Failed,"one failed",[
                new("m1",UnifiedPublishItemStatus.Success,"ok",PublishErrorKind.None,now,now,1),
                new("m2",UnifiedPublishItemStatus.Failed,"network",PublishErrorKind.Recoverable,now,now,3)]));
            repository.SaveFinished(new(request.BatchId,UnifiedPublishItemStatus.Failed,"failed",[],now,now));
            var retry=repository.CreateRetryRequest(request.BatchId,request.Targets);Assert.Equal(request.BatchId,retry.RetryOfBatchId);Assert.Equal(new[]{"m2"},retry.FrozenAssignments!["a1"]);
            Assert.Equal(2,repository.ListDrafts().Single().Items.Count);
        }
        finally{Directory.Delete(root,true);}
    }

    private static PublishBatchRequest Request(int materials,int accounts,MaterialDistributionMode mode)=>new()
    {
        Draft=new PublishDraft{Source=new(){Kind=MaterialSourceKind.CustomFiles,Label="test"},Items=Enumerable.Range(1,materials).Select(index=>new ResolvedMaterial{Id=$"m{index}",Sequence=index,VideoPath=$"v{index}.mp4"}).ToList()},
        Targets=Enumerable.Range(1,accounts).Select(index=>new PublishTarget($"a{index}",$"A{index}",$"p{index}",index)).ToList(),DistributionMode=mode,
    };
}
