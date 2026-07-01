using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChannelsPublisher.Core.Config;
using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Publishing;
using ChannelsPublisher.Core.Services;
using ChannelsPublisher.Desktop.Controls;
using ChannelsPublisher.Desktop.Services;
using ChannelsPublisher.Desktop.ViewModels;

namespace ChannelsPublisher.Desktop.Views;

/// <summary>素材发布视图（可复用 UserControl）：左账号列表 + 右内嵌浏览器 + 底部任务队列。
/// 可独立放进窗口（MainWindow），也可被 ConvertTools 壳作为一个 Tab 承载。</summary>
public partial class MaterialPublishView : UserControl
{
    // 每账号一个内嵌浏览器（都挂在 BrowserArea 里，靠 IsVisible 切换显示，隐藏的仍存活）
    private readonly Dictionary<string, WebView2Host> _hosts = new();
    private readonly PlaywrightPublishAutomation _automation = new();
    private PublishScheduler? _scheduler;
    private MainViewModel? _vm;
    private bool _ready;
    // 发表配置：作为发布的全局默认，供任务队列/发布测试套用；配置对话框保存后重载。
    private PublishConfig _publishConfig = PublishConfig.Load();

    public MaterialPublishView()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new AccountStore()); // 自带 VM，standalone/壳内都可用
        DataContextChanged += OnDataContextChanged;
        // UserControl 无 OnOpened；Loaded 时命名控件与原生窗口句柄已就绪，WebView2 才能挂载
        Loaded += OnLoaded;
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ready = true;
        ApplyConfigToVm();
        ShowAccount(_vm?.SelectedAccount);
    }

    // 把发表配置的运行默认（结束动作）灌进 VM，让队列/发布用配置里的选择起步。
    private void ApplyConfigToVm()
    {
        var vm = _vm;
        if (vm is null) return;
        var fa = _publishConfig.FinalAction switch
        {
            "publish" => FinalAction.Publish,
            "draft" => FinalAction.Draft,
            _ => FinalAction.None,
        };
        var choice = vm.FinalActionChoices.FirstOrDefault(c => c.Value == fa);
        if (choice != null) vm.SelectedFinalAction = choice;
    }

    // 用发表配置填充一条素材的默认表单项（描述/剧集/封面/原创）。
    private void ApplyConfigDefaults(PublishItem item)
    {
        var c = _publishConfig;
        if (c.FillDescription && string.IsNullOrEmpty(item.Description) && !string.IsNullOrWhiteSpace(c.DescriptionTemplate))
        {
            var text = c.DescriptionTemplate.Trim();
            item.Description = c.PrependHash && !text.StartsWith("#") ? "#" + text : text;
        }
        if (string.IsNullOrEmpty(item.DramaName))
        {
            var drama = !string.IsNullOrWhiteSpace(c.NewDramaMountTitle) ? c.NewDramaMountTitle : c.DramaName;
            if (!string.IsNullOrWhiteSpace(drama)) item.DramaName = drama.Trim();
        }
        if (c.ReplaceCover && item.CoverPath is null && !string.IsNullOrWhiteSpace(c.CoverImagePath))
            item.CoverPath = c.CoverImagePath.Trim();
        if (c.DeclareOriginal) item.DeclareOriginal = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.NavigateRequested -= OnNavigateRequested;
        }
        _vm = DataContext as MainViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.NavigateRequested += OnNavigateRequested;
            if (_ready) ShowAccount(_vm.SelectedAccount);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_ready && e.PropertyName == nameof(MainViewModel.SelectedAccount))
            ShowAccount(_vm?.SelectedAccount);
    }

    private void OnNavigateRequested(AccountItemViewModel account, string url)
    {
        if (!_ready) return;
        var host = GetOrCreateHost(account);
        ShowAccount(account);
        host.Navigate(url);
    }

    private void ShowAccount(AccountItemViewModel? account)
    {
        if (!_ready || BrowserArea is null || EmptyHint is null) return;

        foreach (var host in _hosts.Values)
            host.IsVisible = false;

        if (account is null)
        {
            EmptyHint.IsVisible = _hosts.Count == 0;
            return;
        }

        var target = GetOrCreateHost(account);
        target.IsVisible = true;
        EmptyHint.IsVisible = false;
    }

    private WebView2Host GetOrCreateHost(AccountItemViewModel account)
    {
        if (_hosts.TryGetValue(account.Id, out var existing))
            return existing;

        var host = new WebView2Host
        {
            UserDataFolder = account.Model.ProfileDir,
            RemoteDebuggingPort = 9222 + _hosts.Count, // 每账号唯一 CDP 端口
            IsVisible = false,
        };
        host.Ready += () => account.Status = AccountStatus.Online;
        _hosts[account.Id] = host;
        BrowserArea.Children.Add(host);

        host.Navigate(MainViewModel.ChannelsLoginUrl);
        return host;
    }

    // 发布测试：选视频 → 经当前账号 WebView2 的 CDP 端点跑发布流程（只填不发，安全）
    private async void OnPublishClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null) { if (vm != null) vm.StatusMessage = "请先选择账号"; return; }
        if (!_hosts.TryGetValue(account.Id, out var host) || host.CdpEndpoint is null)
        {
            vm.StatusMessage = "该账号内嵌浏览器未就绪（先点「登录」或等待加载完成）";
            return;
        }
        if (Storage is null) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发布的视频",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("视频") { Patterns = new[] { "*.mp4", "*.mov", "*.m4v" } } },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        var item = new PublishItem { VideoPath = file.Path.LocalPath, Description = "【多账号发布测试】" };
        ApplyConfigDefaults(item); // 描述已填则保留测试标记，仅补剧集/封面/原创等配置默认
        _scheduler ??= new PublishScheduler(_automation);
        var job = new AccountPublishJob(account.Model, host.CdpEndpoint!, new[] { item });

        vm.StatusMessage = $"[{account.Name}] 发布中：{item.DisplayName}…";
        try
        {
            await _scheduler.RunAsync(new[] { job }, FinalAction.None, maxParallelAccounts: 1,
                p => Dispatcher.UIThread.Post(() => vm.StatusMessage = $"[{p.AccountName}] {p.ItemName}：{p.Message}"),
                CancellationToken.None);
        }
        catch (Exception ex) { vm.StatusMessage = $"发布失败：{ex.Message}"; }
    }

    // ────────────── 全局配置对话框 ──────────────

    private async void OnPublishConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var ok = await new PublishConfigDialog().ShowDialog<bool>(owner);
        if (ok)
        {
            _publishConfig = PublishConfig.Load(); // 重载并套用新默认
            ApplyConfigToVm();
            if (_vm != null) _vm.StatusMessage = "发表配置已保存（新素材将套用新默认）";
        }
    }

    private async void OnClipConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var ok = await new ClipConfigDialog().ShowDialog<bool>(owner);
        if (ok && _vm != null) _vm.StatusMessage = "全局剪辑配置已保存";
    }

    // ────────────── 发布任务队列 ──────────────

    private async void OnAddMaterialsClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null) { if (vm != null) vm.StatusMessage = "请先选择账号（素材将分配给它）"; return; }
        if (Storage is null) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择素材视频（可多选）",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("视频") { Patterns = new[] { "*.mp4", "*.mov", "*.m4v" } } },
        });
        int n = 0;
        foreach (var f in files)
        {
            var item = new PublishItem { VideoPath = f.Path.LocalPath };
            ApplyConfigDefaults(item);
            vm.Tasks.Add(new PublishTaskItemViewModel(item, account));
            n++;
        }
        if (n > 0) vm.StatusMessage = $"已添加 {n} 条素材到「{account.Name}」";
    }

    private async void OnImportTasksClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null || Storage is null) return;
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入发布任务清单(JSON) — prep 产出",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        PublishTaskFile taskFile;
        try { taskFile = PublishTaskFile.Load(file.Path.LocalPath); }
        catch (Exception ex) { vm.StatusMessage = $"导入失败：{ex.Message}"; return; }

        int added = 0, skipped = 0;
        foreach (var dto in taskFile.Tasks)
        {
            var acct = vm.FindAccount(dto.Account);
            if (acct is null) { skipped++; continue; }
            vm.Tasks.Add(new PublishTaskItemViewModel(dto.ToItem(), acct));
            added++;
        }
        var fa = taskFile.ResolveFinalAction();
        var choice = vm.FinalActionChoices.FirstOrDefault(c => c.Value == fa);
        if (choice != null) vm.SelectedFinalAction = choice;
        vm.StatusMessage = $"已导入 {added} 条任务" + (skipped > 0 ? $"，跳过 {skipped} 条（账号未匹配）" : "");
    }

    private void OnClearDoneClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var done = vm.Tasks.Where(t => t.Status == PublishTaskStatus.Done).ToList();
        foreach (var t in done) vm.Tasks.Remove(t);
        vm.StatusMessage = $"已清空 {done.Count} 条完成任务";
    }

    private async void OnStartPublishClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var pending = vm.Tasks.Where(t => t.Status is PublishTaskStatus.Pending or PublishTaskStatus.Failed).ToList();
        if (pending.Count == 0) { vm.StatusMessage = "没有待发布任务"; return; }

        var jobs = new List<AccountPublishJob>();
        foreach (var group in pending.GroupBy(t => t.Account.Id))
        {
            var acctVm = group.First().Account;
            if (!_hosts.TryGetValue(acctVm.Id, out var host) || host.CdpEndpoint is null)
            {
                foreach (var t in group) { t.Status = PublishTaskStatus.Failed; t.Message = "浏览器未就绪（先登录该账号）"; }
                continue;
            }
            foreach (var t in group) { t.Status = PublishTaskStatus.Pending; t.Message = "排队中"; }
            jobs.Add(new AccountPublishJob(acctVm.Model, host.CdpEndpoint!, group.Select(t => t.Item).ToList()));
        }
        if (jobs.Count == 0) { vm.StatusMessage = "无可发布账号（选中账号的浏览器都未就绪）"; return; }

        _scheduler ??= new PublishScheduler(_automation);
        var finalAction = vm.SelectedFinalAction?.Value ?? FinalAction.None;
        vm.StatusMessage = $"开始发布：{jobs.Count} 账号 / {pending.Count} 素材（{finalAction.ToLabel()}，并发 {vm.MaxParallel}）";
        try
        {
            await _scheduler.RunAsync(jobs, finalAction, vm.MaxParallel,
                p => Dispatcher.UIThread.Post(() => UpdateTaskProgress(p)), CancellationToken.None);
            vm.StatusMessage = "发布结束";
        }
        catch (Exception ex) { vm.StatusMessage = $"发布出错：{ex.Message}"; }
    }

    private void UpdateTaskProgress(PublishProgress p)
    {
        var vm = _vm;
        if (vm is null) return;
        var task = vm.Tasks.FirstOrDefault(t =>
            t.Account.Id == p.AccountId && t.VideoName == p.ItemName && t.Status != PublishTaskStatus.Done);
        if (task is null) return;
        task.Message = p.Message;
        task.Status = p.Done ? (p.Ok ? PublishTaskStatus.Done : PublishTaskStatus.Failed) : PublishTaskStatus.Running;
    }
}
