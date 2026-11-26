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
    public bool ShowsTikTok => showsTikTok;
    public bool ShowsKuaishou => kuaishouDatabasePath is not null;
    public int Maximum => QueueProjectSlotCoordinator.MaxCapacity;

    public event Action<ClientSettings>? TikTokLimitSaved;

    [ObservableProperty] private decimal? _kuaishouProjectConcurrency;
    [ObservableProperty] private decimal? _tikTokProjectConcurrency;
    [ObservableProperty] private int? _videoProcessingLimit;
    [ObservableProperty] private string _saveMessage = "";

    public string VideoProcessingStatus => VideoProcessingLimit is int limit
        ? $"共享视频处理上限：{limit} 路 · 硬件自动"
        : "共享视频处理上限：正在检测硬件…";

    partial void OnVideoProcessingLimitChanged(int? value) =>
        OnPropertyChanged(nameof(VideoProcessingStatus));

    public void Load()
    {
        var errors = new List<string>();
        KuaishouProjectConcurrency = ShowsKuaishou
            ? Read(kuaishouDatabasePath, "快手", settings => settings.KuaishouGlobalProjectConcurrency ?? settings.TiktokGlobalProjectConcurrency)
            : null;
        TikTokProjectConcurrency = ShowsTikTok
            ? Read(tikTokDatabasePath, "TikTok", settings => settings.TiktokGlobalProjectConcurrency)
            : null;
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
        VideoProcessingLimit ??= await VideoProcessingConcurrency.GetLimitAsync();

    [RelayCommand]
    private void Save()
    {
        if ((ShowsKuaishou && !IsValid(KuaishouProjectConcurrency)) ||
            (ShowsTikTok && !IsValid(TikTokProjectConcurrency)))
        {
            SaveMessage = $"项目并发数量必须是 1 到 {Maximum} 之间的整数。";
            return;
        }

        var results = new List<string>();
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

    private bool IsValid(decimal? value) =>
        value is >= 1 && value <= Maximum && decimal.Truncate(value.Value) == value.Value;
}
