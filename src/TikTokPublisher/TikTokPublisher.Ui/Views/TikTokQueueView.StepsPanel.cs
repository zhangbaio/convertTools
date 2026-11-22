using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokQueueView
{
    private const string QueueStepsPreferenceKey = "ui.tiktok_queue_steps_panel";
    private const double MinimumStepsBodyHeight = 40;
    private QueueStepsPreferences _queueStepsPreferences = new();
    private bool _queueStepsPreferencesLoaded;

    private void InitializeQueueStepsPanel()
    {
        foreach (var step in QueueExecutionStepOptions.Children.OfType<CheckBox>())
            step.IsCheckedChanged += (_, _) => UpdateQueueStepsSummary();
        LayoutUpdated += (_, _) => LimitQueueStepsBodyHeight();
        ApplyQueueStepsPresentation();
    }

    private void LoadQueueStepsPanelPreferences()
    {
        if (_queueStepsPreferencesLoaded)
            return;
        _queueStepsPreferencesLoaded = true;
        try
        {
            if (AppSettingStore.TryLoadJson<QueueStepsPreferences>(QueueStepsPreferenceKey, out var saved) &&
                saved is not null)
                _queueStepsPreferences = saved;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"读取 TikTok 执行步骤显示偏好失败：{ex.Message}");
        }
        ApplyQueueStepsPresentation();
    }

    private void SaveQueueStepsPanelPreferences()
    {
        if (!_queueStepsPreferencesLoaded)
            return;
        try
        {
            AppSettingStore.SaveJson(QueueStepsPreferenceKey, _queueStepsPreferences);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"保存 TikTok 执行步骤显示偏好失败：{ex.Message}");
        }
    }

    private void OnQueueStepsToggleClick(object? sender, RoutedEventArgs e)
    {
        _queueStepsPreferences = _queueStepsPreferences with { Expanded = !_queueStepsPreferences.Expanded };
        ApplyQueueStepsPresentation();
        SaveQueueStepsPanelPreferences();
    }

    private void ApplyQueueStepsPresentation()
    {
        QueueStepsFrame.IsVisible = _queueStepsPreferences.Expanded;
        QueueStepsIndicator.RenderTransform = _queueStepsPreferences.Expanded ? new RotateTransform(180) : null;
        ToolTip.SetTip(QueueStepsToggle, _queueStepsPreferences.Expanded ? "收起执行步骤" : "展开执行步骤");
        UpdateQueueStepsSummary();
    }

    private void UpdateQueueStepsSummary()
    {
        var selected = QueueExecutionStepOptions.Children.OfType<CheckBox>()
            .Count(step => step.IsVisible && step.IsChecked == true);
        QueueStepsSummary.Text = $"执行步骤 · 已选 {selected} 项";
        var action = _queueStepsPreferences.Expanded ? "收起" : "展开";
        AutomationProperties.SetName(QueueStepsToggle, $"{action}执行步骤，已选 {selected} 项");
    }

    private void LimitQueueStepsBodyHeight()
    {
        if (!_queueStepsPreferences.Expanded)
            return;

        // The outer toolbar reserves room for the queue table. Spend only its remaining height on the steps.
        var rows = QueueToolbarRows.RowDefinitions;
        var otherRows = rows[0].ActualHeight + rows[1].ActualHeight + rows[2].ActualHeight
            + rows[4].ActualHeight + rows[5].ActualHeight;
        var decorations = VerticalThickness(QueueToolbar.Padding) + VerticalThickness(QueueToolbar.BorderThickness)
            + VerticalThickness(QueueStepsFrame.Padding) + VerticalThickness(QueueStepsFrame.BorderThickness)
            + QueueStepsSummary.DesiredSize.Height;
        var maximum = Math.Max(MinimumStepsBodyHeight, QueueToolbar.MaxHeight - otherRows - decorations);
        if (!QueueStepsBody.MaxHeight.Equals(maximum))
            QueueStepsBody.MaxHeight = maximum;
    }

    private static double VerticalThickness(Thickness thickness) => thickness.Top + thickness.Bottom;

    private sealed record QueueStepsPreferences(bool Expanded = true);
}
