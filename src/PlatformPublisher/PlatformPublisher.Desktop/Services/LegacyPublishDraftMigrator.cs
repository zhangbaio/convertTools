using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Materials;
using PlatformPublisher.Persistence;
using PlatformPublisher.Publishing.Models;
using PlatformPublisher.Publishing.Storage;
using PlatformPublisher.Weixin.Publishing;

namespace PlatformPublisher.Desktop.Services;

public sealed class LegacyPublishDraftMigrator
{
    private const string Marker="unified-publish-legacy-migration-v1";
    private readonly PublishJobStore _jobs;private readonly MaterialDraftFactory _factory;private readonly UnifiedPublishRepository _repository;private readonly IJsonSettingStore _settings;
    public LegacyPublishDraftMigrator(PublishJobStore jobs,MaterialDraftFactory factory,UnifiedPublishRepository repository,IJsonSettingStore settings){_jobs=jobs;_factory=factory;_repository=repository;_settings=settings;}

    public async Task<int> MigrateAsync(CancellationToken cancellationToken=default)
    {
        if(_settings.Load(Marker,()=>false))return 0;var migrated=0;
        foreach(var job in (await _jobs.LoadAsync(cancellationToken)).Where(job=>job.Kind!=PublishJobKind.Series))
        {
            try{var draft=await ConvertAsync(job,cancellationToken);draft.Id="legacy-"+job.Id;draft.CreatedAt=job.CreatedAt;draft.UpdatedAt=job.UpdatedAt;_repository.SaveDraft(draft);migrated++;}catch{ /* 旧任务引用的素材可能已归档；保留旧任务，不阻断启动。 */ }
        }
        _settings.Save(Marker,true);return migrated;
    }

    private async Task<PublishDraft> ConvertAsync(PublishJob job,CancellationToken cancellationToken)
    {
        var kind=job.Kind switch{PublishJobKind.DirectoryMaterials=>MaterialSourceKind.DirectoryGroups,PublishJobKind.ProjectMaterials=>MaterialSourceKind.Project,PublishJobKind.LocalVideos=>MaterialSourceKind.LocalDirectory,PublishJobKind.CustomVideos=>MaterialSourceKind.CustomFiles,PublishJobKind.AdxMaterials=>MaterialSourceKind.AdxBatch,PublishJobKind.SystemHighlight=>MaterialSourceKind.SystemHighlight,_=>throw new InvalidOperationException()};
        var options=WeixinPublishOptions.FromJob(job);var source=new MaterialSourceSpec{Kind=kind,Label="旧素材任务迁移",WorkflowDirectory=job.ProjectDirectory,OriginalTitle=job.ProjectName,NewTitle=job.ProjectName,Files=job.CustomVideoFiles.ToList(),PayloadJson=JsonSerializer.Serialize(new{count=job.PublishCount,videoTypes=job.PublishVideoTypes})};
        var form=new UnifiedPublishForm{OriginalTitle=job.ProjectName,NewTitle=job.ProjectName,SeriesName=job.ProjectName,FillDescription=options.FillDescription,DescriptionTemplate=options.DescriptionTemplate,DeclareOriginal=options.DeclareOriginal,FillShortTitle=options.FillShortTitle,ShortTitleMaxLength=options.ShortTitleMaxLength,LocationOption=options.LocationOptionText,LinkSeries=!string.IsNullOrWhiteSpace(options.LinkOptionText),FinalAction=options.FinalAction=="draft"?UnifiedFinalAction.Draft:UnifiedFinalAction.Publish,StopOnError=options.PauseOnError};
        return await _factory.CreateAsync(source,form,new MediaProcessingProfile(),cancellationToken);
    }
}
