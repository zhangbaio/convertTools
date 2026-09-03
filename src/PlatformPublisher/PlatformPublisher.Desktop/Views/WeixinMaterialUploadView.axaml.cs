using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ChannelsPublisher.Core.Models;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Weixin.Publishing;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Publishing.Models;
using PublishJobKind = PlatformPublisher.Common.Models.PublishJobKind;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinMaterialUploadView : UserControl
{
    private Func<PublishAccount?>? _accountProvider;
    private AdxAutomationService? _adxService;
    private AdxBatchStore? _adxBatchStore;
    private UnifiedPublishViewModel? _unifiedPublishViewModel;
    private Action? _showUnifiedPublish;
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public WeixinMaterialUploadView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Activate();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible) Activate();
        };
    }

    public void Bind(Func<PublishAccount?> accountProvider, AdxAutomationService adxService, AdxBatchStore adxBatchStore,
        UnifiedPublishViewModel unifiedPublishViewModel,Action showUnifiedPublish)
    {
        _accountProvider = accountProvider;
        _adxService = adxService;
        _adxBatchStore = adxBatchStore;
        _unifiedPublishViewModel=unifiedPublishViewModel;
        _showUnifiedPublish=showUnifiedPublish;
        ApplyAccount(accountProvider());
    }

    public void ApplyAccount(PublishAccount? account) =>
        ViewModel?.UseWeixinAccount(account?.Id, account?.Name, account?.ProfileDir);

    private void Activate()
    {
        ViewModel?.ActivateMaterialWorkflow();
        ApplyAccount(_accountProvider?.Invoke());
    }

    private void OnDirectoryMaterialsClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectMaterialJobKind(PublishJobKind.DirectoryMaterials);
    private void OnSystemHighlightClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectMaterialJobKind(PublishJobKind.SystemHighlight);
    private void OnProjectMaterialsClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectMaterialJobKind(PublishJobKind.ProjectMaterials);
    private void OnLocalVideosClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectMaterialJobKind(PublishJobKind.LocalVideos);
    private void OnCustomVideosClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectMaterialJobKind(PublishJobKind.CustomVideos);
    private void OnOpenUnifiedPublishClick(object? sender,RoutedEventArgs e)=>_showUnifiedPublish?.Invoke();

    private async void OnAdxMaterialsClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var account = _accountProvider?.Invoke();
        if (owner is null || ViewModel is null || account is null || _adxService is null || _adxBatchStore is null)
        {
            if (ViewModel is not null) ViewModel.StatusMessage = "请先选择视频号账号。";
            return;
        }
        var selected = ViewModel.SelectedJob;
        var dialog = new AdxMaterialsDialog(
            _adxService, _adxBatchStore, account.Id, account.Name, account.ProfileDir,
            ViewModel.DraftProjectDirectory,
            selected?.OriginalTitle ?? string.Empty,
            selected?.NewTitle ?? selected?.ProjectName ?? string.Empty,
            QueueUnifiedAdxAsync);
        await dialog.ShowDialog(owner);
    }

    private async void OnCreateUnifiedDraftClick(object? sender,RoutedEventArgs e)
    {
        if(ViewModel is null||_unifiedPublishViewModel is null)return;
        try
        {
            var sourceKind=ViewModel.SelectedJobKind.Value switch
            {
                PublishJobKind.DirectoryMaterials=>MaterialSourceKind.DirectoryGroups,
                PublishJobKind.ProjectMaterials=>MaterialSourceKind.Project,
                PublishJobKind.LocalVideos=>MaterialSourceKind.LocalDirectory,
                PublishJobKind.CustomVideos=>MaterialSourceKind.CustomFiles,
                PublishJobKind.SystemHighlight=>MaterialSourceKind.SystemHighlight,
                _=>throw new InvalidOperationException("当前任务类型不能生成素材发布草稿。"),
            };
            var selected=ViewModel.SelectedJob;var title=selected?.NewTitle??selected?.ProjectName??ViewModel.DraftDramaTitle;
            var sourceDirectory=selected?.Model.ProjectDirectory??ViewModel.DraftProjectDirectory;
            if(string.IsNullOrWhiteSpace(title))title=Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
            var source=new MaterialSourceSpec{Kind=sourceKind,Label=ViewModel.SelectedJobKind.Name,WorkflowDirectory=sourceDirectory,
                OriginalTitle=selected?.OriginalTitle??ViewModel.DraftDramaTitle,NewTitle=title,
                Files=ParseFiles(ViewModel.DraftCustomVideoFilesText),PayloadJson=System.Text.Json.JsonSerializer.Serialize(new{count=ViewModel.DraftPublishCount,videoTypes=ViewModel.DraftPublishVideoTypes})};
            await _unifiedPublishViewModel.CreateAndAcceptAsync(source,BuildForm(title),new MediaProcessingProfile());
            ViewModel.StatusMessage=$"已生成统一发布草稿：{title}。";_showUnifiedPublish?.Invoke();
        }
        catch(Exception ex){ViewModel.StatusMessage="生成发布草稿失败："+ex.Message;}
    }

    private async Task QueueUnifiedAdxAsync(AdxPublishPayload payload,string workflowDirectory,string accountId,string accountName,string accountSessionDirectory,bool autoStart)
    {
        if(_unifiedPublishViewModel is null||_adxBatchStore is null)return;
        var items=payload.Items.Select((item,index)=>
        {
            var manifest=_adxBatchStore.Read(item.ManifestPath);return new ResolvedMaterial{Id=item.MaterialId,Sequence=index+1,VideoPath=item.VideoPath,CoverPath=item.CoverPath,Description=item.Description,ShortTitle=item.ShortTitle,Origin=new MaterialOrigin(MaterialSourceKind.AdxBatch,item.MaterialId,manifest?.BatchId??"",item.ManifestPath)};
        }).ToList();
        var options=WeixinPublishOptions.FromJob(new PlatformPublisher.Common.Models.PublishJob{PlatformOptionsJson=payload.PublishOptionsJson});var draft=new PublishDraft{Source=new MaterialSourceSpec{Kind=MaterialSourceKind.AdxBatch,Label="ADX素材",WorkflowDirectory=workflowDirectory,OriginalTitle=payload.OriginalTitle,NewTitle=payload.NewTitle,Files=items.Select(item=>item.VideoPath).ToList()},Items=items,Form=BuildForm(payload.NewTitle,options)};
        await _unifiedPublishViewModel.AcceptDraftAsync(draft,autoStart);_showUnifiedPublish?.Invoke();
    }

    private UnifiedPublishForm BuildForm(string title,WeixinPublishOptions? options=null)
    {
        options??=WeixinPublishOptions.FromJob(new PlatformPublisher.Common.Models.PublishJob{PlatformOptionsJson=ViewModel?.DraftPlatformOptionsJson??"",PublishDescription=ViewModel?.DraftPublishDescription??"",DeclareOriginal=ViewModel?.DraftDeclareOriginal??true,HideLocation=ViewModel?.DraftHideLocation??true});
        return new UnifiedPublishForm{OriginalTitle=ViewModel?.SelectedJob?.OriginalTitle??ViewModel?.DraftDramaTitle??"",NewTitle=title,SeriesName=title,
            DescriptionTemplate=options.DescriptionTemplate,FillDescription=options.FillDescription,DeclareOriginal=options.DeclareOriginal,
            FillShortTitle=options.FillShortTitle,ShortTitleMaxLength=options.ShortTitleMaxLength,LinkSeries=!string.IsNullOrWhiteSpace(options.LinkOptionText),
            LocationOption=options.LocationOptionText,FinalAction=options.FinalAction=="publish"?UnifiedFinalAction.Publish:UnifiedFinalAction.Draft,StopOnError=options.PauseOnError};
    }

    private static List<string> ParseFiles(string text)=>text.Split(['\r','\n',';','|'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToList();

    private async void OnPickProjectDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择素材上传工作目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0) ViewModel.DraftProjectDirectory = folders[0].Path.LocalPath;
    }

    private async void OnPickCustomVideosClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发表的视频",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("视频文件") { Patterns = ["*.mp4", "*.mov", "*.m4v", "*.mkv", "*.avi", "*.flv", "*.wmv", "*.webm"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0) return;
        var paths = files.Select(file => file.Path.LocalPath).ToArray();
        ViewModel.DraftCustomVideoFilesText = string.Join(Environment.NewLine, paths);
        if (!Directory.Exists(ViewModel.DraftProjectDirectory))
            ViewModel.DraftProjectDirectory = Path.GetDirectoryName(paths[0]) ?? string.Empty;
    }

    private async void OnAdvancedConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || ViewModel is null) return;
        var draftJob = new PlatformPublisher.Common.Models.PublishJob
        {
            PlatformOptionsJson = ViewModel.DraftPlatformOptionsJson,
            PublishDescription = ViewModel.DraftPublishDescription,
            DeclareOriginal = ViewModel.DraftDeclareOriginal,
            HideLocation = ViewModel.DraftHideLocation,
        };
        var result = await WeixinPublishConfigDialog.ShowAsync(owner, WeixinPublishOptions.FromJob(draftJob));
        if (result is null) return;
        ViewModel.DraftPlatformOptionsJson = result.ToJson();
        ViewModel.DraftPublishDescription = result.DescriptionTemplate;
        ViewModel.DraftDeclareOriginal = result.DeclareOriginal;
        ViewModel.DraftHideLocation = !string.IsNullOrWhiteSpace(result.LocationOptionText);
        ViewModel.StatusMessage = "视频号素材发表配置已应用到新任务。";
    }
}
