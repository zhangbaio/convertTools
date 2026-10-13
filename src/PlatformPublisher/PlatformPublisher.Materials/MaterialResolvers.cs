using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Publishing.Models;

namespace PlatformPublisher.Materials;

public interface IMaterialSourceResolver
{
    MaterialSourceKind Kind { get; }
    Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken);
}

public sealed class MaterialResolverRegistry
{
    private readonly IReadOnlyDictionary<MaterialSourceKind,IMaterialSourceResolver> _resolvers;
    public MaterialResolverRegistry(IEnumerable<IMaterialSourceResolver> resolvers)=>_resolvers=resolvers.ToDictionary(item=>item.Kind);
    public Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken)=>
        _resolvers.TryGetValue(source.Kind,out var resolver)?resolver.ResolveAsync(source,cancellationToken):throw new InvalidOperationException($"未注册素材解析器：{source.Kind}");
}

public abstract class FileMaterialResolverBase : IMaterialSourceResolver
{
    private static readonly string[] VideoExtensions=[".mp4",".mov",".m4v",".mkv",".avi",".flv",".ts",".wmv",".webm"];
    public abstract MaterialSourceKind Kind{get;}
    public abstract Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken);
    protected static IReadOnlyList<ResolvedMaterial> Build(MaterialSourceSpec source,IEnumerable<string> files,bool finalized=false)
    {
        return files.Where(path=>!string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Where(File.Exists).Where(IsVideo).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(NaturalToken,StringComparer.OrdinalIgnoreCase).ThenBy(path=>path,StringComparer.OrdinalIgnoreCase).Select((path,index)=>
        {
            var stem=Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path));
            var sidecar=ReadSidecar(stem+".publish.json");
            return new ResolvedMaterial{Id=Text(sidecar,"materialId")??StableId(path),Sequence=index+1,VideoPath=path,CoverPath=ResolveCover(stem,Text(sidecar,"coverPath")),Description=Text(sidecar,"description"),ShortTitle=Text(sidecar,"shortTitle"),ContentFinalized=finalized,Origin=new MaterialOrigin(source.Kind,SourceId:path,PayloadJson:source.PayloadJson)};
        }).ToArray();
    }
    protected static bool IsVideo(string path)=>VideoExtensions.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase);
    protected static IEnumerable<string> Top(string directory)=>Directory.Exists(directory)?Directory.EnumerateFiles(directory,"*.*",SearchOption.TopDirectoryOnly).Where(IsVideo):[];
    private static string? ResolveCover(string stem,string? configured){if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))return Path.GetFullPath(configured);foreach(var ext in new[]{".cover.jpg",".cover.jpeg",".cover.png",".jpg",".jpeg",".png",".webp"})if(File.Exists(stem+ext))return stem+ext;return null;}
    private static JsonElement? ReadSidecar(string path){try{return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();}catch{return null;}}
    private static string? Text(JsonElement? root,string name)=>root is{ValueKind:JsonValueKind.Object}value&&value.TryGetProperty(name,out var property)&&property.ValueKind==JsonValueKind.String?property.GetString():null;
    private static string StableId(string path)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()+"|"+new FileInfo(path).Length)))[..20].ToLowerInvariant();
    private static string NaturalToken(string path){var digits=new string(Path.GetFileNameWithoutExtension(path).Where(char.IsDigit).ToArray());return long.TryParse(digits,out var value)?value.ToString("D16"):Path.GetFileName(path);}
}

public sealed class CustomFileMaterialResolver:FileMaterialResolverBase
{
    public override MaterialSourceKind Kind=>MaterialSourceKind.CustomFiles;
    public override Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken)=>Task.FromResult(Build(source,source.Files));
}

public sealed class LocalDirectoryMaterialResolver:FileMaterialResolverBase
{
    public override MaterialSourceKind Kind=>MaterialSourceKind.LocalDirectory;
    public override Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken)=>Task.FromResult(Build(source,Top(source.WorkflowDirectory)));
}

public sealed class ProjectMaterialResolver:FileMaterialResolverBase
{
    public override MaterialSourceKind Kind=>MaterialSourceKind.Project;
    public override Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken)
    {
        var materials=Top(Path.Combine(source.WorkflowDirectory,"material-videos")).ToArray();var videos=Top(Path.Combine(source.WorkflowDirectory,"videos")).ToArray();
        return Task.FromResult(Build(source,materials.Length>0?materials:videos));
    }
}

public sealed class DirectoryGroupMaterialResolver:FileMaterialResolverBase
{
    public override MaterialSourceKind Kind=>MaterialSourceKind.DirectoryGroups;
    public override Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken)
    {
        if(!Directory.Exists(source.WorkflowDirectory))return Task.FromResult<IReadOnlyList<ResolvedMaterial>>([]);
        var files=Directory.EnumerateDirectories(source.WorkflowDirectory).Select(directory=>Top(directory).OrderByDescending(path=>new FileInfo(path).Length).FirstOrDefault()).Where(path=>path is not null)!;
        var result=Build(source,files!);foreach(var item in result){var directory=Path.GetDirectoryName(item.VideoPath)!;var description=new[]{"description.txt","desc.txt","描述.txt"}.Select(name=>Path.Combine(directory,name)).FirstOrDefault(File.Exists);if(description is not null)item.Description=File.ReadAllText(description).Trim();}
        return Task.FromResult(result);
    }
}

public sealed class DownloadedWorkMaterialResolver:FileMaterialResolverBase
{
    public override MaterialSourceKind Kind=>MaterialSourceKind.DownloadedWork;
    public override Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken)=>Task.FromResult(Build(source,source.Files,finalized:true));
}

public sealed class AdxMaterialResolver:IMaterialSourceResolver
{
    private readonly AdxBatchStore _store;public AdxMaterialResolver(AdxBatchStore store)=>_store=store;public MaterialSourceKind Kind=>MaterialSourceKind.AdxBatch;
    public Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken)
    {
        var selected=source.Files.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);var result=_store.List(source.WorkflowDirectory).SelectMany(batch=>batch.Items.Select(item=>(batch,item))).Where(value=>selected.Count==0||selected.Contains(Path.GetFullPath(value.item.VideoPath))).GroupBy(value=>value.item.MaterialId,StringComparer.OrdinalIgnoreCase).Select(group=>group.OrderByDescending(value=>value.batch.CreatedAt).First()).OrderBy(value=>value.item.Rank).Select((value,index)=>new ResolvedMaterial{Id=value.item.MaterialId,Sequence=index+1,VideoPath=value.item.VideoPath,CoverPath=value.item.CoverPath,Description=value.item.Description,ShortTitle=value.item.ShortTitle,Origin=new MaterialOrigin(Kind,SourceId:value.item.MaterialId,BatchId:value.batch.BatchId,ManifestPath:value.batch.ManifestPath)}).ToArray();
        return Task.FromResult<IReadOnlyList<ResolvedMaterial>>(result);
    }
}

public sealed class SystemHighlightMaterialResolver:IMaterialSourceResolver
{
    private sealed record Payload(int Count,string VideoTypes);
    public MaterialSourceKind Kind=>MaterialSourceKind.SystemHighlight;
    public Task<IReadOnlyList<ResolvedMaterial>> ResolveAsync(MaterialSourceSpec source,CancellationToken cancellationToken)
    {
        Payload payload;try{payload=JsonSerializer.Deserialize<Payload>(source.PayloadJson,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new(1,"混剪,解说,切片");}catch{payload=new(1,"混剪,解说,切片");}
        var values=Enumerable.Range(1,Math.Clamp(payload.Count,1,100)).Select(index=>new ResolvedMaterial{Id=$"system-highlight-{index}",Sequence=index,VideoPath=$"platform://system-highlight/{Uri.EscapeDataString(source.NewTitle)}/{index}",Origin=new MaterialOrigin(Kind,SourceId:index.ToString(),PayloadJson:source.PayloadJson)}).ToArray();return Task.FromResult<IReadOnlyList<ResolvedMaterial>>(values);
    }
}

public sealed class MaterialDraftFactory
{
    private readonly MaterialResolverRegistry _registry;public MaterialDraftFactory(MaterialResolverRegistry registry)=>_registry=registry;
    public async Task<PublishDraft> CreateAsync(MaterialSourceSpec source,UnifiedPublishForm form,MediaProcessingProfile media,CancellationToken cancellationToken)
    {
        var items=await _registry.ResolveAsync(source,cancellationToken);if(items.Count==0)throw new InvalidOperationException($"素材来源“{source.Label}”没有可发布内容。");
        form.OriginalTitle=string.IsNullOrWhiteSpace(form.OriginalTitle)?source.OriginalTitle:form.OriginalTitle;form.NewTitle=string.IsNullOrWhiteSpace(form.NewTitle)?source.NewTitle:form.NewTitle;form.SeriesName=string.IsNullOrWhiteSpace(form.SeriesName)?form.NewTitle:form.SeriesName;
        return new PublishDraft{Source=source,Items=items.ToList(),Form=form,MediaProcessing=media};
    }
}
