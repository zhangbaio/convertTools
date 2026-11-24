using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouAuditTimeoutDialog : Window
{
    private IReadOnlyList<KuaishouAuditTimeoutRow> _rows = [];

    public KuaishouAuditTimeoutDialog() => InitializeComponent();

    public static Task<KuaishouAuditTimeoutTarget[]?> ShowAsync(
        Window owner, string accountName, IReadOnlyList<KuaishouAuditTimeoutTarget> targets)
    {
        var dialog = new KuaishouAuditTimeoutDialog();
        dialog.AccountText.Text = $"账号「{accountName}」 · 找到 {targets.Count} 部";
        dialog._rows = targets.Select(target => new KuaishouAuditTimeoutRow(target)).ToArray();
        foreach (var row in dialog._rows)
            row.PropertyChanged += (_, _) => dialog.UpdateSelection();
        dialog.CandidatesList.ItemsSource = dialog._rows;
        dialog.UpdateSelection();
        return dialog.ShowDialog<KuaishouAuditTimeoutTarget[]?>(owner);
    }

    private void UpdateSelection()
    {
        var selected = Selection();
        var incompatible = selected.Length > 1 &&
                           selected.Any(target => target.Eligibility == KuaishouAuditTimeoutEligibility.MixedSummary);
        SelectionText.Text = incompatible ? "混合提示请单独勾选一部。" : $"已勾选 {selected.Length} 部";
        SubmitButton.IsEnabled = selected.Length > 0 && !incompatible;
    }

    private KuaishouAuditTimeoutTarget[] Selection() => _rows
        .Where(row => row.IsSelected && row.Target.CanSelect)
        .Select(row => row.Target)
        .ToArray();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (SubmitButton.IsEnabled)
            Close(Selection());
    }
}

public sealed partial class KuaishouAuditTimeoutRow(KuaishouAuditTimeoutTarget target) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public KuaishouAuditTimeoutTarget Target { get; } = target;
    public string Caption => $"{Target.Title}（{Target.SeriesId}）";
    public string Status => $"{Target.EligibilityLabel} · {Target.EpisodeLabel}";
    public string Reasons => string.Join("；", Target.Reasons);
}
