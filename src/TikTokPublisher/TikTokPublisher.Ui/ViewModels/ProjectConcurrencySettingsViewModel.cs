using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class ProjectConcurrencySettingsViewModel(
    string? tikTokDatabasePath,
    string? kuaishouDatabasePath,
    bool showsTikTok) : ViewModelBase
{
    private VideoProcessingConcurrencySettings _savedVideoSettings = new();

    public bool ShowsTikTok => showsTikTok;
    public bool ShowsKuaishou => kuaishouDatabasePath is not null;
    public int Maximum => QueueProjectSlotCoordinator.MaxCapacity;
    public int VideoManualMaximum => VideoProcessingConcurrency.ManualMaximum;

    public event Action<ClientSettings>? TikTokLimitSaved;

    [ObservableProperty] private decimal? _kuaishouProjectConcurrency;
    [ObservableProperty] private decimal? _tikTokProjectConcurrency;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VideoProcessingStatus))]
    private bool _videoAutomatic = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VideoProcessingStatus))]
    private decimal? _videoManualLimit = 2;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VideoProcessingStatus))]
    private int? _videoRecommendedLimit;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VideoProcessingStatus))]
    private int _activeVideoLanes;
    [ObservableProperty] private string _saveMessage = "";

    public string VideoProcessingStatus
    {
        get
        {
            var recommendation = VideoRecommendedLimit is int limit
                ? $"硬件推荐 {limit} 路"
                : "正在检测硬件";
            var active = _savedVideoSettings.Automatic
                ? VideoRecommendedLimit is int automaticLimit ? $"当前生效 {automaticLimit} 路（自动）" : "自动模式"
                : $"当前生效 {_savedVideoSettings.ManualLimit} 路（手动）";
            var pending = VideoAutomatic != _savedVideoSettings.Automatic ||
                          (!VideoAutomatic && VideoManualLimit != _savedVideoSettings.ManualLimit);
            return $"{active} · 转码占用 {ActiveVideoLanes} 路 · {recommendation}" +
                   (pending ? " · 上限修改未保存" : "");
        }
    }

    public void Load()
    {
        var errors = new List<string>();
        KuaishouProjectConcurrency = ShowsKuaishou
            ? Read(kuaishouDatabasePath, "快手", settings => settings.KuaishouGlobalProjectConcurrency ?? settings.TiktokGlobalProjectConcurrency)
            : null;
        TikTokProjectConcurrency = ShowsTikTok
            ? Read(tikTokDatabasePath, "TikTok", settings => settings.TiktokGlobalProjectConcurrency)
            : null;
        _savedVideoSettings = VideoProcessingConcurrency.LoadSettings();
        VideoAutomatic = _savedVideoSettings.Automatic;
        VideoManualLimit = _savedVideoSettings.ManualLimit;
        RefreshVideoProcessingUsage();
        OnPropertyChanged(nameof(VideoProcessingStatus));
        SaveMessage = string.Join("。", errors);

        decimal? Read(string? databasePath, string platform, Func<ClientSettings, int> select)
        {
            try
            {
                return QueueProjectSlotCoordinator.NormalizeCapacity(select(ClientSettingsStore.Load(databasePath)));
            }
            catch (Exception ex)
            {
                errors.Add($"{platform} 项目并发读取失败：{ex.Message}");
                return null;
            }
        }
    }

    public async Task RefreshVideoProcessingLimitAsync() =>
        VideoRecommendedLimit ??= await VideoProcessingConcurrency.GetLimitAsync();

    public void RefreshVideoProcessingUsage() => ActiveVideoLanes = VideoProcessingConcurrency.ActiveLanes;

    partial void OnVideoAutomaticChanged(bool value) => SaveMessage = "";

    partial void OnVideoManualLimitChanged(decimal? value) => SaveMessage = "";

    [RelayCommand]
    private async Task SaveAsync()
    {
        if ((ShowsKuaishou && !IsValid(KuaishouProjectConcurrency)) ||
            (ShowsTikTok && !IsValid(TikTokProjectConcurrency)))
        {
            SaveMessage = $"项目并发数量必须是 1 到 {Maximum} 之间的整数。";
            return;
        }

        var validManualLimit = IsValid(VideoManualLimit, VideoManualMaximum);
        if (!VideoAutomatic && !validManualLimit)
        {
            SaveMessage = $"共享视频处理上限必须是 1 到 {VideoManualMaximum} 之间的整数。";
            return;
        }
        var videoSettings = new VideoProcessingConcurrencySettings(VideoAutomatic,
            validManualLimit ? (int)VideoManualLimit.GetValueOrDefault() : _savedVideoSettings.ManualLimit);

        var results = new List<string>();
        if (videoSettings != _savedVideoSettings)
        {
            try
            {
                await VideoProcessingConcurrency.SaveSettingsAsync(videoSettings);
                _savedVideoSettings = videoSettings;
                VideoManualLimit = videoSettings.ManualLimit;
                if (videoSettings.Automatic)
                    await RefreshVideoProcessingLimitAsync();
                RefreshVideoProcessingUsage();
                OnPropertyChanged(nameof(VideoProcessingStatus));
                var appliedLimit = videoSettings.Automatic ? VideoRecommendedLimit : videoSettings.ManualLimit;
                results.Add($"共享视频处理上限已保存为 {appliedLimit} 路");
            }
            catch (Exception ex)
            {
                results.Add($"共享视频处理上限保存失败：{ex.Message}");
            }
        }
        if (ShowsKuaishou)
            Update("快手", (int)KuaishouProjectConcurrency!.Value, kuaishou: true, kuaishouDatabasePath);
        if (ShowsTikTok)
            Update("TikTok", (int)TikTokProjectConcurrency!.Value, kuaishou: false, tikTokDatabasePath);
        SaveMessage = string.Join("。", results) + "。";

        void Update(string platform, int limit, bool kuaishou, string? databasePath)
        {
            var saved = kuaishou ? null : TikTokLimitSaved;
            ClientSettings? settings = null;
            try
            {
                if (saved is not null)
                    settings = ClientSettingsStore.Load(databasePath);
                ClientSettingsStore.SaveProjectConcurrency(limit, kuaishou, databasePath);
            }
            catch (Exception ex)
            {
                results.Add($"{platform} 保存失败：{ex.Message}");
                return;
            }

            results.Add($"{platform} 已保存");
            if (settings is not null)
            {
                settings.TiktokGlobalProjectConcurrency = limit;
                saved?.Invoke(settings);
            }
        }
    }

    private bool IsValid(decimal? value) => IsValid(value, Maximum);

    private static bool IsValid(decimal? value, int maximum) =>
        value is >= 1 && value <= maximum && decimal.Truncate(value.Value) == value.Value;
}
