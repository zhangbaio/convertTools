using Avalonia.Controls;
using Avalonia.Interactivity;
using ChannelsPublisher.Core.Models;
using PlatformPublisher.Desktop.Services;

namespace PlatformPublisher.Desktop.Views;

public partial class LegacySessionImportDialog : Window
{
    private readonly LegacyAccountSessionImportService? _service;
    private readonly List<PublishAccount> _targets = [];
    private readonly List<PublishAccount> _imported = [];

    public LegacySessionImportDialog() { InitializeComponent(); }

    public LegacySessionImportDialog(LegacyAccountSessionImportService service,
        IEnumerable<PublishAccount> targetAccounts) : this()
    {
        _service = service;
        _targets.AddRange(targetAccounts);
        SourceRootInput.Text = service.DefaultLegacyRoot;
        TargetAccountInput.ItemsSource = _targets;
        TargetAccountInput.SelectedIndex = _targets.Count > 0 ? 0 : -1;
        RefreshCandidates();
    }

    public IReadOnlyList<PublishAccount> ImportedAccounts => _imported;

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => RefreshCandidates();

    private void RefreshCandidates()
    {
        if (_service is null) return;
        try
        {
            var candidates = _service.Discover(SourceRootInput.Text);
            LegacyAccountInput.ItemsSource = candidates;
            LegacyAccountInput.SelectedIndex = candidates.Count > 0 ? 0 : -1;
            StatusText.Text = $"发现 {candidates.Count} 个包含登录状态的旧账号。";
            ResultText.Text = string.Empty;
        }
        catch (Exception ex) { StatusText.Text = "扫描失败：" + ex.Message; }
    }

    private void OnLegacySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LegacyAccountInput.SelectedItem is not LegacyAccountSessionCandidate candidate) return;
        LegacySummaryText.Text = candidate.Summary;
        WeixinInput.IsChecked = candidate.Weixin.Exists;
        WeixinInput.IsEnabled = candidate.Weixin.Exists;
        KuaishouPersonalInput.IsChecked = candidate.KuaishouPersonal.Exists;
        KuaishouPersonalInput.IsEnabled = candidate.KuaishouPersonal.Exists;
        KuaishouEnterpriseInput.IsChecked = candidate.KuaishouEnterprise.Exists;
        KuaishouEnterpriseInput.IsEnabled = candidate.KuaishouEnterprise.Exists;
        var exact = _targets.FirstOrDefault(account =>
            string.Equals(account.Name, candidate.Name, StringComparison.CurrentCultureIgnoreCase));
        if (exact is not null)
        {
            TargetAccountInput.SelectedItem = exact;
            CreateAccountInput.IsChecked = false;
        }
    }

    private async void OnImportCurrentClick(object? sender, RoutedEventArgs e)
    {
        if (_service is null || LegacyAccountInput.SelectedItem is not LegacyAccountSessionCandidate candidate) return;
        var target = CreateAccountInput.IsChecked == true
            ? _service.CreateTargetAccount(candidate.Name)
            : TargetAccountInput.SelectedItem as PublishAccount;
        if (target is null)
        {
            StatusText.Text = "请选择目标账号，或勾选创建同名新账号。";
            return;
        }
        await ImportOneAsync(candidate, target);
        if (!_targets.Contains(target))
        {
            _targets.Add(target);
            TargetAccountInput.ItemsSource = null;
            TargetAccountInput.ItemsSource = _targets;
        }
        TargetAccountInput.SelectedItem = target;
        CreateAccountInput.IsChecked = false;
    }

    private async void OnImportAllClick(object? sender, RoutedEventArgs e)
    {
        if (_service is null || LegacyAccountInput.ItemsSource is not IEnumerable<LegacyAccountSessionCandidate> candidates) return;
        SetBusy(true);
        var lines = new List<string>();
        try
        {
            foreach (var candidate in candidates)
            {
                var target = _targets.FirstOrDefault(account =>
                                 string.Equals(account.Name, candidate.Name, StringComparison.CurrentCultureIgnoreCase))
                             ?? _service.CreateTargetAccount(candidate.Name);
                if (!_targets.Contains(target)) _targets.Add(target);
                try
                {
                    var result = await _service.ImportAsync(candidate, target,
                        new LegacySessionImportSelection(candidate.Weixin.Exists,
                            candidate.KuaishouPersonal.Exists, candidate.KuaishouEnterprise.Exists));
                    TrackImported(target);
                    lines.Add($"{candidate.Name} → {target.Name}：{string.Join("、", result.Platforms)}");
                }
                catch (Exception ex) { lines.Add($"{candidate.Name}：失败，{ex.Message}"); }
            }
            ResultText.Text = string.Join(Environment.NewLine, lines);
            StatusText.Text = $"导入完成：成功 {_imported.Count} 个账号。";
        }
        finally { SetBusy(false); }
    }

    private async Task ImportOneAsync(LegacyAccountSessionCandidate candidate, PublishAccount target)
    {
        if (_service is null) return;
        SetBusy(true);
        try
        {
            var result = await _service.ImportAsync(candidate, target, new LegacySessionImportSelection(
                WeixinInput.IsChecked == true, KuaishouPersonalInput.IsChecked == true,
                KuaishouEnterpriseInput.IsChecked == true));
            TrackImported(target);
            ResultText.Text = $"{candidate.Name} → {target.Name}：已导入 {string.Join("、", result.Platforms)}。";
            StatusText.Text = "登录态快照导入完成；打开对应平台时会进行实际登录校验。";
        }
        catch (Exception ex) { StatusText.Text = "导入失败：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private void TrackImported(PublishAccount target)
    {
        if (_imported.All(account => account.Id != target.Id)) _imported.Add(target);
    }

    private void SetBusy(bool busy)
    {
        LegacyAccountInput.IsEnabled = !busy;
        TargetAccountInput.IsEnabled = !busy;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(_imported.Count > 0);
}
