using PlatformPublisher.Materials;
using PlatformPublisher.Publishing.Models;
using Xunit;

namespace PlatformPublisher.Materials.Tests;

public sealed class MaterialResolverTests
{
    [Fact]
    public async Task Project_prefers_material_videos_and_reads_sidecar()
    {
        var root=Temp();try
        {
            var materials=Directory.CreateDirectory(Path.Combine(root,"material-videos")).FullName;var videos=Directory.CreateDirectory(Path.Combine(root,"videos")).FullName;
            await File.WriteAllTextAsync(Path.Combine(materials,"第2集.mp4"),"22");await File.WriteAllTextAsync(Path.Combine(materials,"第1集.mp4"),"1");await File.WriteAllTextAsync(Path.Combine(videos,"ignored.mp4"),"x");
            await File.WriteAllTextAsync(Path.Combine(materials,"第1集.publish.json"),"{\"description\":\"第一集描述\",\"shortTitle\":\"第一集\"}");await File.WriteAllTextAsync(Path.Combine(materials,"第1集.cover.jpg"),"cover");
            var result=await new ProjectMaterialResolver().ResolveAsync(new(){Kind=MaterialSourceKind.Project,WorkflowDirectory=root},default);
            Assert.Equal(2,result.Count);Assert.Equal("第1集.mp4",Path.GetFileName(result[0].VideoPath));Assert.Equal("第一集描述",result[0].Description);Assert.NotNull(result[0].CoverPath);
        }finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Directory_groups_choose_largest_video_and_description()
    {
        var root=Temp();try
        {
            var group=Directory.CreateDirectory(Path.Combine(root,"01")).FullName;await File.WriteAllTextAsync(Path.Combine(group,"small.mp4"),"1");await File.WriteAllTextAsync(Path.Combine(group,"large.mp4"),"12345");await File.WriteAllTextAsync(Path.Combine(group,"description.txt"),"分组描述");
            var item=Assert.Single(await new DirectoryGroupMaterialResolver().ResolveAsync(new(){Kind=MaterialSourceKind.DirectoryGroups,WorkflowDirectory=root},default));Assert.Equal("large.mp4",Path.GetFileName(item.VideoPath));Assert.Equal("分组描述",item.Description);
        }finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task System_highlight_creates_virtual_materials()
    {
        var values=await new SystemHighlightMaterialResolver().ResolveAsync(new(){Kind=MaterialSourceKind.SystemHighlight,NewTitle="剧名",PayloadJson="{\"count\":3,\"videoTypes\":\"混剪\"}"},default);
        Assert.Equal(3,values.Count);Assert.All(values,item=>Assert.StartsWith("platform://system-highlight/",item.VideoPath));
    }

    private static string Temp(){var path=Path.Combine(Path.GetTempPath(),"material-resolver-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
}
