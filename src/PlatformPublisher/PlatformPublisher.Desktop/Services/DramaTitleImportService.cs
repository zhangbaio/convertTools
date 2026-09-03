using PlatformPublisher.Common.Services;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Services;

public sealed record DramaTitleImportOutcome(
    IReadOnlyList<string> ProjectDirectories,
    IReadOnlyList<UploadTitleImportFailure> Failures,
    int RequestedCount);

public sealed class DramaTitleImportService
{
    public const int MinimumEpisodes = 10;
    public const int MaximumEpisodes = 200;

    public async Task<DramaTitleImportOutcome> ImportAsync(
        string workspaceDirectory,
        string titlesText,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var workspace=Path.GetFullPath(workspaceDirectory);
        if(!Directory.Exists(workspace))throw new DirectoryNotFoundException($"工作目录不存在：{workspace}");
        var (requests,parseFailures)=UploadTitleImportService.ParseRequests(titlesText,UploadTitleImportService.MatchModeTitle);
        if(requests.Count==0)throw new InvalidOperationException("请输入至少一个短剧名称。");

        var settings=ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        ShortDramaDramaServices.RefreshSettings(settings);
        var failures=new List<UploadTitleImportFailure>(parseFailures);
        var projectDirectories=new List<string>();
        for(var index=0;index<requests.Count;index++)
        {
            cancellationToken.ThrowIfCancellationRequested();var request=requests[index];
            log?.Invoke($"精确搜索短剧 {index+1}/{requests.Count}：{request.Title}");
            try
            {
                var coreItems=await ShortDramaDramaServices.Search.SearchAsync(request.Title,1,cancellationToken);
                var items=coreItems.Select(ShortDramaDramaServices.FromCore).ToArray();
                var (matched,reason)=UploadTitleImportService.PickPreferredSearchMatch(request.Title,items);
                if(matched is null){failures.Add(new(request.Title,reason));log?.Invoke($"未加入：{request.Title}，{reason}");continue;}
                var episodeError=matched.EpisodeTotal<MinimumEpisodes?$"集数 {matched.EpisodeTotal}，小于最小限制 {MinimumEpisodes}":matched.EpisodeTotal>MaximumEpisodes?$"集数 {matched.EpisodeTotal}，大于最大限制 {MaximumEpisodes}":string.Empty;
                if(!string.IsNullOrWhiteSpace(episodeError)){failures.Add(new(request.Title,episodeError));log?.Invoke($"已过滤：{request.Title}，{episodeError}");continue;}
                var directory=await ShortDramaDramaServices.BootstrapAsync(workspace,matched,"all",settings.DramaDownloadDefaultQuality,settings.DramaDownloadConcurrent,"source","short",cancellationToken);
                projectDirectories.Add(directory);log?.Invoke($"已创建/更新项目：{matched.Title}（{matched.EpisodeTotal} 集）");
            }
            catch(Exception ex) when(ex is not OperationCanceledException){failures.Add(new(request.Title,ex.Message));log?.Invoke($"导入失败：{request.Title}，{ex.Message}");}
        }
        return new(projectDirectories,failures,requests.Count);
    }
}
