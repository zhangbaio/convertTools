using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Materials;
using PlatformPublisher.Publishing.Execution;
using PlatformPublisher.Publishing.Models;
using PlatformPublisher.Publishing.Storage;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class UnifiedPublishViewModel : ObservableObject
{
    private readonly MaterialDraftFactory _draftFactory;
    private readonly UnifiedPublishRepository _repository;
    private readonly PublishBatchCoordinator _coordinator;
    private Func<IReadOnlyList<PublishTarget>> _accountProvider = () => [];
    private PublishDraft? _draft;
    private CancellationTokenSource? _cts;
    private bool _accountsInitialized;

    public UnifiedPublishViewModel(MaterialDraftFactory draftFactory,UnifiedPublishRepository repository,PublishBatchCoordinator coordinator)
    {
        _draftFactory=draftFactory;_repository=repository;_coordinator=coordinator;
        _selectedSourceChoice=SourceChoices[0];_selectedDistributionChoice=DistributionChoices[0];
        _selectedFinalActionChoice=FinalActionChoices[0];_selectedFailureChoice=FailureChoices[0];
        ResolveCommand=new AsyncRelayCommand(ResolveAsync,()=>!IsBusy);
        StartCommand=new AsyncRelayCommand(StartAsync,()=>!IsBusy);
        StopCommand=new RelayCommand(()=>_cts?.Cancel(),()=>IsBusy);
        RetryCommand=new AsyncRelayCommand(RetryAsync,()=>!IsBusy&&SelectedHistory is not null);
        RefreshCommand=new RelayCommand(Refresh);
        SelectAllAccountsCommand=new RelayCommand(()=>SetAllAccounts(true));
        ClearAccountsCommand=new RelayCommand(()=>SetAllAccounts(false));
    }

    public ObservableCollection<UnifiedAccountRow> Accounts{get;}=[];
    public ObservableCollection<UnifiedMaterialRow> Materials{get;}=[];
    public ObservableCollection<UnifiedPublishHistoryRow> History{get;}=[];
    public IReadOnlyList<UnifiedChoice<MaterialSourceKind>> SourceChoices{get;}=
    [
        new("项目素材",MaterialSourceKind.Project),new("本地目录",MaterialSourceKind.LocalDirectory),
        new("目录分组",MaterialSourceKind.DirectoryGroups),new("自选文件",MaterialSourceKind.CustomFiles),
        new("ADX素材",MaterialSourceKind.AdxBatch),new("系统高光",MaterialSourceKind.SystemHighlight),
        new("已下载作品",MaterialSourceKind.DownloadedWork),
    ];
    public IReadOnlyList<UnifiedChoice<MaterialDistributionMode>> DistributionChoices{get;}=
    [new("每账号全部素材",MaterialDistributionMode.Broadcast),new("按账号均衡分配",MaterialDistributionMode.Balanced)];
    public IReadOnlyList<UnifiedChoice<UnifiedFinalAction>> FinalActionChoices{get;}=
    [new("保存草稿",UnifiedFinalAction.Draft),new("直接发表",UnifiedFinalAction.Publish)];
    public IReadOnlyList<UnifiedChoice<PublishFailurePolicy>> FailureChoices{get;}=
    [new("单账号失败继续",PublishFailurePolicy.Continue),new("任一失败停止全部",PublishFailurePolicy.StopAll)];

    public IAsyncRelayCommand ResolveCommand{get;}
    public IAsyncRelayCommand StartCommand{get;}
    public IRelayCommand StopCommand{get;}
    public IAsyncRelayCommand RetryCommand{get;}
    public IRelayCommand RefreshCommand{get;}
    public IRelayCommand SelectAllAccountsCommand{get;}
    public IRelayCommand ClearAccountsCommand{get;}

    [ObservableProperty] private UnifiedChoice<MaterialSourceKind> _selectedSourceChoice;
    [ObservableProperty] private UnifiedChoice<MaterialDistributionMode> _selectedDistributionChoice;
    [ObservableProperty] private UnifiedChoice<UnifiedFinalAction> _selectedFinalActionChoice;
    [ObservableProperty] private UnifiedChoice<PublishFailurePolicy> _selectedFailureChoice;
    [ObservableProperty] private string _workflowDirectory=string.Empty;
    [ObservableProperty] private string _selectedFilesText=string.Empty;
    [ObservableProperty] private string _originalTitle=string.Empty;
    [ObservableProperty] private string _newTitle=string.Empty;
    [ObservableProperty] private string _descriptionTemplate="热门短剧，精彩内容持续更新。";
    [ObservableProperty] private bool _declareOriginal=true;
    [ObservableProperty] private bool _fillDescription=true;
    [ObservableProperty] private bool _fillShortTitle;
    [ObservableProperty] private bool _linkSeries;
    [ObservableProperty] private bool _mediaProcessingEnabled;
    [ObservableProperty] private bool _clearMetadata=true;
    [ObservableProperty] private bool _zoomCrop=true;
    [ObservableProperty] private bool _colorAdjust=true;
    [ObservableProperty] private bool _speedAdjust=true;
    [ObservableProperty] private int _maxParallelAccounts=2;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage="请选择素材来源并解析。";
    [ObservableProperty] private string _progressText="0/0";
    [ObservableProperty] private UnifiedPublishHistoryRow? _selectedHistory;

    public void BindAccounts(Func<IReadOnlyList<PublishTarget>> accountProvider){_accountProvider=accountProvider;Refresh();}
    public void SetWorkflowDirectory(string path){WorkflowDirectory=path;InvalidateDraft();}
    public void SetSelectedFiles(IEnumerable<string> paths){SelectedFilesText=string.Join(Environment.NewLine,paths);InvalidateDraft();}

    public void LoadDraft(PublishDraft draft)
    {
        SelectedSourceChoice=SourceChoices.First(item=>item.Value==draft.Source.Kind);WorkflowDirectory=draft.Source.WorkflowDirectory;
        SelectedFilesText=string.Join(Environment.NewLine,draft.Source.Files);OriginalTitle=draft.Form.OriginalTitle;NewTitle=draft.Form.NewTitle;
        DescriptionTemplate=draft.Form.DescriptionTemplate;DeclareOriginal=draft.Form.DeclareOriginal;FillDescription=draft.Form.FillDescription;
        FillShortTitle=draft.Form.FillShortTitle;LinkSeries=draft.Form.LinkSeries;SelectedFinalActionChoice=FinalActionChoices.First(item=>item.Value==draft.Form.FinalAction);
        MediaProcessingEnabled=draft.MediaProcessing.Enabled;ClearMetadata=draft.MediaProcessing.ClearMetadata;ZoomCrop=draft.MediaProcessing.ZoomCrop;
        ColorAdjust=draft.MediaProcessing.ColorAdjust;SpeedAdjust=draft.MediaProcessing.SpeedAdjust;_draft=draft;ShowMaterials(draft);StatusMessage=$"已载入草稿：{draft.Items.Count} 条素材。";
    }

    public async Task AcceptDraftAsync(PublishDraft draft,bool start=false)
    {
        _repository.SaveDraft(draft);LoadDraft(draft);Refresh();if(start)await StartAsync();
    }

    public async Task CreateAndAcceptAsync(MaterialSourceSpec source,UnifiedPublishForm form,MediaProcessingProfile? media=null,bool start=false)
    {
        var draft=await _draftFactory.CreateAsync(source,form,media??new MediaProcessingProfile(),CancellationToken.None);
        await AcceptDraftAsync(draft,start);
    }

    private async Task ResolveAsync()
    {
        try{IsBusy=true;var source=BuildSource();var form=BuildForm();var media=BuildMedia();_draft=await _draftFactory.CreateAsync(source,form,media,CancellationToken.None);_repository.SaveDraft(_draft);ShowMaterials(_draft);StatusMessage=$"已解析并保存 {Materials.Count} 条素材。";RefreshHistory();}
        catch(Exception ex){StatusMessage="解析失败："+ex.Message;}finally{IsBusy=false;NotifyCommands();}
    }

    private async Task StartAsync()
    {
        try
        {
            IsBusy=true;NotifyCommands();if(_draft is null)await ResolveForStartAsync();if(_draft is null)return;
            var targets=SelectedTargets();if(targets.Count==0)throw new InvalidOperationException("请至少选择一个发布账号。");
            _draft.Form=BuildForm();_draft.MediaProcessing=BuildMedia();_repository.SaveDraft(_draft);_cts=new CancellationTokenSource();
            var request=new PublishBatchRequest{Draft=_draft,Targets=targets,DistributionMode=SelectedDistributionChoice.Value,FailurePolicy=SelectedFailureChoice.Value,MaxParallelAccounts=Math.Clamp(MaxParallelAccounts,1,8)};
            var progress=new Progress<UnifiedPublishProgress>(value=>{StatusMessage=value.Message;ProgressText=$"{value.Completed}/{value.Total}";});
            var outcome=await _coordinator.ExecuteAsync(request,progress,_cts.Token);StatusMessage=outcome.Message;
        }
        catch(OperationCanceledException){StatusMessage="发布已停止，完成记录已保留。";}
        catch(Exception ex){StatusMessage="发布失败："+ex.Message;}
        finally{_cts?.Dispose();_cts=null;IsBusy=false;NotifyCommands();RefreshHistory();}
    }

    private async Task RetryAsync()
    {
        if(SelectedHistory is null)return;try{IsBusy=true;NotifyCommands();_cts=new CancellationTokenSource();var request=_repository.CreateRetryRequest(SelectedHistory.BatchId,_accountProvider());request.MaxParallelAccounts=Math.Clamp(MaxParallelAccounts,1,8);LoadDraft(request.Draft);var progress=new Progress<UnifiedPublishProgress>(value=>StatusMessage=value.Message);var outcome=await _coordinator.ExecuteAsync(request,progress,_cts.Token);StatusMessage="重试完成："+outcome.Message;}
        catch(OperationCanceledException){StatusMessage="重试已停止。";}catch(Exception ex){StatusMessage="重试失败："+ex.Message;}finally{_cts?.Dispose();_cts=null;IsBusy=false;NotifyCommands();RefreshHistory();}
    }

    private async Task ResolveForStartAsync(){var source=BuildSource();_draft=await _draftFactory.CreateAsync(source,BuildForm(),BuildMedia(),CancellationToken.None);_repository.SaveDraft(_draft);ShowMaterials(_draft);}
    private MaterialSourceSpec BuildSource()=>new(){Kind=SelectedSourceChoice.Value,Label=SelectedSourceChoice.Label,WorkflowDirectory=WorkflowDirectory.Trim(),OriginalTitle=OriginalTitle.Trim(),NewTitle=NewTitle.Trim(),Files=ParseFiles().ToList(),PayloadJson=SelectedSourceChoice.Value==MaterialSourceKind.SystemHighlight?"{\"count\":10,\"videoTypes\":\"混剪,解说,切片\"}":"{}"};
    private UnifiedPublishForm BuildForm()=>new(){OriginalTitle=OriginalTitle.Trim(),NewTitle=NewTitle.Trim(),SeriesName=NewTitle.Trim(),DescriptionTemplate=DescriptionTemplate,DeclareOriginal=DeclareOriginal,FillDescription=FillDescription,FillShortTitle=FillShortTitle,LinkSeries=LinkSeries,LinkSeriesName=NewTitle.Trim(),FinalAction=SelectedFinalActionChoice.Value,StopOnError=SelectedFailureChoice.Value==PublishFailurePolicy.StopAll};
    private MediaProcessingProfile BuildMedia()=>new(){Enabled=MediaProcessingEnabled,ClearMetadata=ClearMetadata,ZoomCrop=ZoomCrop,ColorAdjust=ColorAdjust,SpeedAdjust=SpeedAdjust};
    private List<PublishTarget> SelectedTargets()=>Accounts.Where(item=>item.IsSelected).Select(item=>item.Target).OrderBy(item=>item.Order).ToList();
    private IEnumerable<string> ParseFiles()=>SelectedFilesText.Split(['\r','\n',';','|'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
    private void ShowMaterials(PublishDraft draft){Materials.Clear();foreach(var item in draft.Items)Materials.Add(new(item.Sequence,Path.GetFileName(item.VideoPath),item.VideoPath,item.Description??""));ProgressText=$"0/{draft.Items.Count}";}
    private void Refresh(){RefreshAccounts();RefreshHistory();}
    private void RefreshAccounts(){var selected=Accounts.Where(item=>item.IsSelected).Select(item=>item.Target.AccountId).ToHashSet(StringComparer.OrdinalIgnoreCase);Accounts.Clear();foreach(var target in _accountProvider().OrderBy(item=>item.Order))Accounts.Add(new(target,!_accountsInitialized||selected.Contains(target.AccountId)));_accountsInitialized=true;}
    private void RefreshHistory(){History.Clear();foreach(var item in _repository.ListHistory())History.Add(new(item.BatchId,item.Status,item.Message,item.StartedAt.ToLocalTime().ToString("MM-dd HH:mm"),item.RetryOf));}
    private void SetAllAccounts(bool selected){foreach(var account in Accounts)account.IsSelected=selected;}
    private void InvalidateDraft(){_draft=null;Materials.Clear();}
    private void NotifyCommands(){ResolveCommand.NotifyCanExecuteChanged();StartCommand.NotifyCanExecuteChanged();StopCommand.NotifyCanExecuteChanged();RetryCommand.NotifyCanExecuteChanged();}
    partial void OnSelectedHistoryChanged(UnifiedPublishHistoryRow? value)=>RetryCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)=>NotifyCommands();
    partial void OnSelectedSourceChoiceChanged(UnifiedChoice<MaterialSourceKind> value)=>InvalidateDraft();
    partial void OnWorkflowDirectoryChanged(string value)=>InvalidateDraft();
    partial void OnSelectedFilesTextChanged(string value)=>InvalidateDraft();

    public sealed record UnifiedChoice<T>(string Label,T Value);
}

public sealed partial class UnifiedAccountRow : ObservableObject
{
    public UnifiedAccountRow(PublishTarget target,bool selected){Target=target;_isSelected=selected;}
    public PublishTarget Target{get;}
    public string Name=>Target.AccountName;
    public string AccountId=>Target.AccountId;
    [ObservableProperty] private bool _isSelected;
}

public sealed record UnifiedMaterialRow(int Sequence,string Name,string Path,string Description);
public sealed record UnifiedPublishHistoryRow(string BatchId,string Status,string Message,string StartedAt,string? RetryOf);
