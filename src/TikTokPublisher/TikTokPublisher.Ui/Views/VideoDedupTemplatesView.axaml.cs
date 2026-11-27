using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class VideoDedupTemplatesView : UserControl
{
    private MainViewModel? _vm;
    private TikTokAccountProfile? _account;
    private VideoDedupTemplateSnapshot? _snapshot;
    private VideoDedupTemplateEntry? _selected;
    private string _initialFingerprint = "";
    private string _initialName = "";
    private bool _confirmingChanges;
    private bool _loadingSelection;

    public VideoDedupTemplatesView() => InitializeComponent();

    public void Bind(MainViewModel viewModel)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = viewModel;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsVisible && e.PropertyName == nameof(MainViewModel.SelectedAccount))
            RefreshSelectedAccount();
    }

    public void RefreshSelectedAccount()
    {
        _account = _vm?.SelectedAccount?.Model;
        ReloadTemplates();
    }

    private void ReloadTemplates(string? selectedId = null)
    {
        if (_vm is null) return;
        try
        {
            _snapshot = _vm.LoadVideoDedupTemplates();
            var current = _snapshot.DefaultFor(_account?.Id);
            AccountSummary.Text = _account is null ? "请选择 TikTok 账号" :
                $"当前账号 · {_account.DisplayName}　默认模板 · {current.Name}";
            WorkspaceForm.IsEnabled = true;
            LoadEditor(_snapshot.Templates.FirstOrDefault(item => item.Id == selectedId) ?? current);
            FeedbackText.Text = "";
        }
        catch (Exception ex)
        {
            _snapshot = null;
            LoadEditor(null);
            AccountSummary.Text = "视频去重模板库读取失败";
            WorkspaceForm.IsEnabled = false;
            FeedbackText.Text = ex.Message;
        }
    }

    private void LoadEditor(VideoDedupTemplateEntry? template, VideoDedupPreset? draft = null, string? draftName = null)
    {
        _selected = template;
        _loadingSelection = true;
        try
        {
            var items = _snapshot?.Templates.Select(entry => new ComboBoxItem { Content = entry.Name, Tag = entry.Id }).ToArray() ?? [];
            TemplateSelector.ItemsSource = items;
            TemplateSelector.SelectedItem = items.FirstOrDefault(item => Equals(item.Tag, template?.Id));
        }
        finally { _loadingSelection = false; }

        var editable = _snapshot is not null && template?.Id != VideoDedupTemplateCatalog.SystemId;
        TemplateName.IsEnabled = PresetEditor.IsEnabled = SaveButton.IsEnabled = editable;
        DeleteButton.IsEnabled = template is { Id: not VideoDedupTemplateCatalog.SystemId };
        ApplyButton.IsEnabled = template is not null && _account is not null;
        TemplateName.Text = draftName ?? template?.Name ?? "";
        PresetEditor.Load(draft ?? template?.Preset);
        _initialName = TemplateName.Text ?? "";
        _initialFingerprint = PresetEditor.CaptureDraft().Fingerprint();

        if (template is null || _snapshot is null || template.Id == VideoDedupTemplateCatalog.SystemId)
        {
            UsageSummary.Text = "";
            return;
        }
        var count = _snapshot.AccountDefaults.Values.Count(id => id == template.Id);
        UsageSummary.Text = count == 0 ? "暂无账号使用" : $"供 {count} 个账号使用";
        var names = _vm?.Accounts.Where(account => _snapshot.AccountDefaults.GetValueOrDefault(account.Id) == template.Id)
            .Select(account => account.DisplayName).ToArray() ?? [];
        ToolTip.SetTip(UsageSummary, names.Length > 0 ? string.Join("、", names) : null);
    }

    public async Task<bool> TryLeaveAsync(Window owner)
    {
        if (_confirmingChanges) return false;
        if (!HasUnsavedChanges()) return true;

        _confirmingChanges = true;
        try
        {
            var save = await ConfirmDialog.ShowSaveChangesAsync(owner, "保存去重模板", "当前模板有未保存的修改。是否保存？");
            if (save is null) return false;
            if (save == true) return SaveTemplate();
            ReloadTemplates(_selected?.Id);
            return true;
        }
        finally { _confirmingChanges = false; }
    }

    private bool HasUnsavedChanges() => SaveButton.IsEnabled &&
        ((TemplateName.Text?.Trim() ?? "") != _initialName.Trim() ||
         PresetEditor.CaptureDraft().Fingerprint() != _initialFingerprint);

    private async void OnTemplateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection || (TemplateSelector.SelectedItem as ComboBoxItem)?.Tag is not string id || id == _selected?.Id)
            return;
        if (HasUnsavedChanges() && TopLevel.GetTopLevel(this) is Window owner && !await TryLeaveAsync(owner))
        {
            _loadingSelection = true;
            try { TemplateSelector.SelectedItem = TemplateSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _selected?.Id)); }
            finally { _loadingSelection = false; }
            return;
        }
        ReloadTemplates(id);
    }

    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryLeaveAsync(owner)) return;
        LoadEditor(null, VideoDedupPreset.CreateDefault());
        TemplateName.Focus();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryLeaveAsync(owner)) return;
        var source = _selected;
        LoadEditor(null, source?.Preset?.Clone() ?? VideoDedupPreset.CreateDefault(), source is null ? "" : source.Name + " · 副本");
        TemplateName.Focus();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => SaveTemplate();

    private bool SaveTemplate()
    {
        if (_vm is null || !SaveButton.IsEnabled) return false;
        try
        {
            var saved = VideoDedupTemplateCatalog.OpenDefault().Save(_selected?.Id, TemplateName.Text ?? "", PresetEditor.Read());
            ReloadTemplates(saved.Id);
            FeedbackText.Text = $"模板「{saved.Name}」已保存。";
            return true;
        }
        catch (Exception ex)
        {
            FeedbackText.Text = $"保存失败：{ex.Message}";
            return false;
        }
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || _account is not { } account || _selected is not { } template ||
            !ReferenceEquals(account, vm.SelectedAccount?.Model)) return;
        if (HasUnsavedChanges())
        {
            FeedbackText.Text = "请先保存当前模板的修改。";
            return;
        }
        try
        {
            VideoDedupTemplateCatalog.OpenDefault().SetDefault(account.Id, template.Id);
            ReloadTemplates(template.Id);
            FeedbackText.Text = $"「{account.DisplayName}」默认使用「{template.Name}」。";
        }
        catch (Exception ex) { FeedbackText.Text = $"设置默认模板失败：{ex.Message}"; }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || _selected is not { } entry || entry.Id == VideoDedupTemplateCatalog.SystemId ||
            TopLevel.GetTopLevel(this) is not Window owner || !await TryLeaveAsync(owner)) return;
        try
        {
            var snapshot = vm.LoadVideoDedupTemplates();
            var bound = snapshot.AccountDefaults.Values.Count(id => id == entry.Id);
            string? replacement = null;
            if (bound > 0)
            {
                replacement = await VideoDedupTemplatePicker.SelectAsync(owner, "选择替代模板",
                    $"有 {bound} 个账号使用「{entry.Name}」。确认后，这些账号将改用所选模板，并删除当前模板。",
                    snapshot.Templates.Where(item => item.Id != entry.Id).ToArray(), VideoDedupTemplateCatalog.SystemId);
                if (replacement is null) return;
            }
            else if (!await ConfirmDialog.ShowAsync(owner, "删除去重模板", $"确认删除「{entry.Name}」？"))
                return;

            VideoDedupTemplateCatalog.OpenDefault().Delete(entry.Id, replacement);
            ReloadTemplates();
            FeedbackText.Text = "去重模板已删除。";
        }
        catch (Exception ex) { FeedbackText.Text = $"删除模板失败：{ex.Message}"; }
    }
}
