using PlatformPublisher.Publishing.Models;

namespace PlatformPublisher.Publishing.Distribution;

public static class MaterialDistributionPlanner
{
    public static IReadOnlyList<AccountPublishPlan> Build(PublishBatchRequest request)
    {
        if (request.Targets.Count == 0) throw new InvalidOperationException("请选择至少一个发布账号。");
        if (request.Draft.Items.Count == 0) throw new InvalidOperationException("发布草稿中没有素材。");
        var targets=request.Targets.OrderBy(item=>item.Order).GroupBy(item=>item.AccountId,StringComparer.OrdinalIgnoreCase).Select(group=>group.First()).ToArray();
        Dictionary<string,List<ResolvedMaterial>> assignments;
        if(request.FrozenAssignments is not null)
        {
            var byId=request.Draft.Items.ToDictionary(item=>item.Id,StringComparer.OrdinalIgnoreCase);
            assignments=request.FrozenAssignments.ToDictionary(pair=>pair.Key,pair=>pair.Value.Where(byId.ContainsKey).Select(id=>byId[id]).ToList(),StringComparer.OrdinalIgnoreCase);
            Validate(request.Draft.Items,targets,assignments,requireComplete:false);
        }
        else if(request.DistributionMode==MaterialDistributionMode.Broadcast)
            assignments=targets.ToDictionary(target=>target.AccountId,_=>request.Draft.Items.OrderBy(item=>item.Sequence).ToList(),StringComparer.OrdinalIgnoreCase);
        else
        {
            if(request.Draft.Items.Count<targets.Length)throw new InvalidOperationException($"均分发布要求素材数不少于账号数：当前{request.Draft.Items.Count}个素材、{targets.Length}个账号。");
            assignments=new(StringComparer.OrdinalIgnoreCase);var offset=0;var quotient=request.Draft.Items.Count/targets.Length;var remainder=request.Draft.Items.Count%targets.Length;
            for(var index=0;index<targets.Length;index++){var count=quotient+(index<remainder?1:0);assignments[targets[index].AccountId]=request.Draft.Items.OrderBy(item=>item.Sequence).Skip(offset).Take(count).ToList();offset+=count;}
            Validate(request.Draft.Items,targets,assignments,requireComplete:true);
        }
        return targets.Select(target=>new AccountPublishPlan(target,assignments.GetValueOrDefault(target.AccountId)??[],request.Draft.Source,request.Draft.Form,request.Draft.MediaProcessing)).ToArray();
    }

    public static Dictionary<string,List<string>> Freeze(IEnumerable<AccountPublishPlan> plans)=>plans.ToDictionary(plan=>plan.Target.AccountId,plan=>plan.Items.Select(item=>item.Id).ToList(),StringComparer.OrdinalIgnoreCase);

    private static void Validate(IReadOnlyList<ResolvedMaterial> source,IReadOnlyList<PublishTarget> targets,Dictionary<string,List<ResolvedMaterial>> assignments,bool requireComplete)
    {
        var targetIds=targets.Select(item=>item.AccountId).ToHashSet(StringComparer.OrdinalIgnoreCase);if(assignments.Keys.Any(key=>!targetIds.Contains(key)))throw new InvalidOperationException("分配结果包含未选择账号。");
        if(targets.Any(target=>!assignments.TryGetValue(target.AccountId,out var items)||items.Count==0))throw new InvalidOperationException("每个发布账号必须分配至少一条素材。");
        var sourceIds=source.Select(item=>item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);var assigned=assignments.Values.SelectMany(value=>value).ToArray();
        if(assigned.Any(item=>!sourceIds.Contains(item.Id)))throw new InvalidOperationException("分配结果包含原草稿范围外的素材。");
        if(requireComplete&&(assigned.Select(item=>item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=source.Count||assigned.Length!=source.Count))throw new InvalidOperationException("均分结果存在素材遗漏或重复。");
    }
}
