using System.Collections.ObjectModel;
using ChannelsPublisher.Clip;
using CommunityToolkit.Mvvm.ComponentModel;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

internal sealed partial class MaterialClipQueueItem(
    string projectFolder, string title, IReadOnlyList<EpisodeSource> episodes, string outputFolder,
    MaterialClipTaskConfiguration configuration) : ObservableObject
{
    public string ProjectFolder { get; } = projectFolder;
    public string Title { get; } = title;
    public IReadOnlyList<EpisodeSource> Episodes { get; } = episodes;
    public string Description => $"{Episodes.Count} 集 · {string.Join(" / ", Configuration.EnabledModes
        .Select(mode => MaterialClipModeEditor.Label(mode, Configuration.StoryHighlights)))} · {Outputs.Count} 条成片";
    public string OutputFolder { get; set; } = outputFolder;
    public MaterialClipTaskConfiguration Configuration { get; set; } = configuration;
    public bool Pending { get; private set; } = true;
    public bool Running { get; private set; }
    public ClipEngineOptions? LastRun { get; private set; }
    public bool CanRetry => !Pending && !Running && LastRun is not null && Status != "已完成";
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<MaterialClipOutput> Outputs { get; } = [];

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private string _status = "待剪辑";

    public ClipEngineOptions CreateRun(MaterialClipConnections services, ClientSettings client) =>
        Configuration.BuildOptions(services, client, Title, OutputFolder);

    public void BeginRun(ClipEngineOptions options)
    {
        LastRun = options;
        Running = true;
        Pending = false;
        Status = "正在准备";
        Logs.Clear();
        Outputs.Clear();
        RefreshDescription();
    }

    public void Finish(string status)
    {
        Running = false;
        Status = status;
    }

    public void PrepareForNewRun()
    {
        LastRun = null;
        Pending = true;
        Status = "待剪辑";
        Logs.Clear();
        Outputs.Clear();
        RefreshDescription();
    }

    public void MarkConfigurationEdited()
    {
        if (CanRetry)
            RefreshDescription();
        else
            PrepareForNewRun();
    }

    public void RefreshDescription() => OnPropertyChanged(nameof(Description));
}
