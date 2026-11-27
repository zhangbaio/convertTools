using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokQueueView
{
    private string? _pendingDedupTemplateId;
    private int _dedupSelectionVersion;

    private void RefreshDedupTemplateSummary()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshDedupTemplateSummary);
            return;
        }
        if (_vm is not { } vm || !vm.QueueVideoDedupEnabled) return;
        try
        {
            var catalog = vm.LoadVideoDedupTemplates();
            if (_pendingDedupTemplateId is { } id)
            {
                var selected = catalog.Templates.FirstOrDefault(template => template.Id == id);
                DedupTemplateSummary.Text = selected is not null ? $"本批模板 · {selected.Name}" : "本批模板已删除，请重新选择";
            }
            else DedupTemplateSummary.Text = $"按账号默认 · 当前 {catalog.DefaultFor(vm.SelectedAccount?.Id).Name}";
        }
        catch (Exception ex) { DedupTemplateSummary.Text = $"去重模板读取失败：{ex.Message}"; }
    }

    private async void OnChooseDedupTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var version = _dedupSelectionVersion;
        try
        {
            var catalog = vm.LoadVideoDedupTemplates();
            var entries = new[] { new VideoDedupTemplateEntry("", "按各项目所属账号的默认模板", null) }
                .Concat(catalog.Templates).ToArray();
            var chosen = await VideoDedupTemplatePicker.SelectAsync(owner, "选择本批去重模板",
                "当前勾选项目可统一使用所选模板。下一批恢复各账号的默认选择。",
                entries, _pendingDedupTemplateId ?? "");
            if (chosen is null || version != _dedupSelectionVersion) return;
            _pendingDedupTemplateId = chosen.Length == 0 ? null : chosen;
            _dedupSelectionVersion++;
            RefreshDedupTemplateSummary();
        }
        catch (Exception ex) { vm.StatusMessage = $"无法选择去重模板：{ex.Message}"; }
    }
}
