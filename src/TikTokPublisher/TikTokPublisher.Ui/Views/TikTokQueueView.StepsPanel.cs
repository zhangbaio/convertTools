using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokQueueView
{
    private const string QueueStepsPreferenceKey = "ui.tiktok_queue_steps_panel";
    private const double MinimumStepsBodyHeight = 40;
    private const double KeyboardResizeIncrement = 16;
    private QueueStepsPreferences _queueStepsPreferences = new();
    private bool _queueStepsPreferencesLoaded;
    private IPointer? _stepsResizePointer;
    private double _stepsResizeOriginY;
    private double _stepsResizeStartHeight;
    private bool _stepsResizeMoved;

    private void InitializeQueueStepsPanel()
    {
        foreach (var step in QueueExecutionStepOptions.Children.OfType<CheckBox>())
            step.IsCheckedChanged += (_, _) => UpdateQueueStepsSummary();
        LayoutUpdated += (_, _) => ApplyQueueStepsHeight();
        UpdateQueueStepsSummary();
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
            {
                var height = saved.Height;
                _queueStepsPreferences = saved with
                {
                    Height = height is double value && double.IsFinite(value) && value > 0 ? value : null,
                };
            }
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
        QueueStepsBody.IsVisible = _queueStepsPreferences.Expanded;
        QueueStepsResizeHandle.IsVisible = _queueStepsPreferences.Expanded;
        QueueStepsIndicator.Text = _queueStepsPreferences.Expanded ? "▾" : "▸";
        UpdateQueueStepsSummary();
        ApplyQueueStepsHeight();
    }

    private void UpdateQueueStepsSummary()
    {
        var selected = QueueExecutionStepOptions.Children.OfType<CheckBox>()
            .Count(step => step.IsVisible && step.IsChecked == true);
        QueueStepsSummary.Text = $"执行步骤 · 已选 {selected} 项";
        var action = _queueStepsPreferences.Expanded ? "收起" : "展开";
        AutomationProperties.SetName(QueueStepsToggle, $"{action}执行步骤，已选 {selected} 项");
    }

    private (double Minimum, double Maximum) GetQueueStepsHeightLimits()
    {
        // The outer toolbar reserves room for the queue table. Spend only its remaining height on the steps.
        var rows = QueueToolbarRows.RowDefinitions;
        var otherRows = rows[0].ActualHeight + rows[1].ActualHeight + rows[2].ActualHeight + rows[4].ActualHeight;
        var decorations = VerticalThickness(QueueToolbar.Padding) + VerticalThickness(QueueToolbar.BorderThickness)
            + VerticalThickness(QueueStepsFrame.Padding) + VerticalThickness(QueueStepsFrame.BorderThickness)
            + QueueStepsToggle.DesiredSize.Height + QueueStepsResizeHandle.Height;
        var remaining = Math.Max(MinimumStepsBodyHeight, QueueToolbar.MaxHeight - otherRows - decorations);
        var natural = QueueStepsContent.DesiredSize.Height;
        var maximum = natural > 0 ? Math.Min(remaining, natural) : remaining;
        return (Math.Min(MinimumStepsBodyHeight, maximum), maximum);
    }

    private static double VerticalThickness(Thickness thickness) => thickness.Top + thickness.Bottom;

    private void ApplyQueueStepsHeight()
    {
        if (!_queueStepsPreferences.Expanded)
            return;
        var (minimum, maximum) = GetQueueStepsHeightLimits();
        var height = _queueStepsPreferences.Height is double requested
            ? Math.Clamp(requested, minimum, maximum)
            : double.NaN;
        if (!QueueStepsBody.MaxHeight.Equals(maximum))
            QueueStepsBody.MaxHeight = maximum;
        if (!QueueStepsBody.Height.Equals(height))
            QueueStepsBody.Height = height;
    }

    private void OnQueueStepsResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_stepsResizePointer is not null || !e.GetCurrentPoint(QueueStepsResizeHandle).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        QueueStepsResizeHandle.Focus();
        if (e.ClickCount >= 2)
        {
            ResetQueueStepsHeight();
            return;
        }
        _stepsResizePointer = e.Pointer;
        _stepsResizeOriginY = e.GetPosition(this).Y;
        _stepsResizeStartHeight = QueueStepsBody.Bounds.Height;
        _stepsResizeMoved = false;
        e.Pointer.Capture(QueueStepsResizeHandle);
    }

    private void OnQueueStepsResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _stepsResizePointer))
            return;
        var delta = e.GetPosition(this).Y - _stepsResizeOriginY;
        if (!_stepsResizeMoved && Math.Abs(delta) < 1)
            return;
        _stepsResizeMoved |= ResizeQueueStepsTo(_stepsResizeStartHeight + delta);
        e.Handled = true;
    }

    private void OnQueueStepsResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _stepsResizePointer))
            return;
        FinishQueueStepsResize();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnQueueStepsResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _stepsResizePointer))
            FinishQueueStepsResize();
    }

    private void FinishQueueStepsResize()
    {
        _stepsResizePointer = null;
        if (_stepsResizeMoved)
            SaveQueueStepsPanelPreferences();
        _stepsResizeMoved = false;
    }

    private void OnQueueStepsResizeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home)
        {
            ResetQueueStepsHeight();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            var delta = e.Key == Key.Up ? -KeyboardResizeIncrement : KeyboardResizeIncrement;
            if (ResizeQueueStepsTo(QueueStepsBody.Bounds.Height + delta))
                SaveQueueStepsPanelPreferences();
            e.Handled = true;
        }
    }

    private bool ResizeQueueStepsTo(double height)
    {
        var (minimum, maximum) = GetQueueStepsHeightLimits();
        var resized = Math.Clamp(height, minimum, maximum);
        var current = double.IsNaN(QueueStepsBody.Height) ? QueueStepsBody.Bounds.Height : QueueStepsBody.Height;
        if (Math.Abs(resized - current) < 0.5)
            return false;
        _queueStepsPreferences = _queueStepsPreferences with { Height = resized };
        ApplyQueueStepsHeight();
        return true;
    }

    private void ResetQueueStepsHeight()
    {
        _queueStepsPreferences = _queueStepsPreferences with { Height = null };
        ApplyQueueStepsHeight();
        SaveQueueStepsPanelPreferences();
    }

    private sealed record QueueStepsPreferences(bool Expanded = true, double? Height = null);
}
