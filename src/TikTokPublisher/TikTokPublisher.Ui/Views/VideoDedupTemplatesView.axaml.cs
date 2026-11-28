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

        SaveButton.IsEnabled = _snapshot is not null && _account is not null;
        var editable = SaveButton.IsEnabled && template?.Id != VideoDedupTemplateCatalog.SystemId;
        TemplateName.IsEnabled = PresetEditor.IsEnabled = editable;
        DeleteButton.IsEnabled = template is { Id: not VideoDedupTemplateCatalog.SystemId };
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

    public Task<bool> TryLeaveAsync(Window owner) =>
        ConfirmChangesAsync(owner, HasUnsavedChanges(), discardSelection: true);

    private Task<bool> TryChangeTemplateAsync(Window owner) =>
        ConfirmChangesAsync(owner, HasTemplateChanges(), discardSelection: false);

    private async Task<bool> ConfirmChangesAsync(Window owner, bool changed, bool discardSelection)
    {
        if (_confirmingChanges) return false;
        if (!changed) return true;

        var account = _account;
        _confirmingChanges = true;
        try
        {
            var save = await ConfirmDialog.ShowSaveChangesAsync(owner, "保存去重配置", "当前账号有未保存的去重配置。是否保存？");
            if (save is null || !ReferenceEquals(account, _account)) return false;
            if (save == true) return SaveConfiguration();
            ReloadTemplates(discardSelection ? null : _selected?.Id);
            return true;
        }
        finally { _confirmingChanges = false; }
    }

    private bool HasUnsavedChanges() => SaveButton.IsEnabled &&
        (HasTemplateChanges() || _selected?.Id != _snapshot?.DefaultFor(_account?.Id).Id);

    private bool HasTemplateChanges() => PresetEditor.IsEnabled &&
        (_selected is null || (TemplateName.Text?.Trim() ?? "") != _initialName.Trim() ||
         PresetEditor.CaptureDraft().Fingerprint() != _initialFingerprint);

    private async void OnTemplateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection || (TemplateSelector.SelectedItem as ComboBoxItem)?.Tag is not string id || id == _selected?.Id)
            return;
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryChangeTemplateAsync(owner))
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
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryChangeTemplateAsync(owner)) return;
        LoadEditor(null, VideoDedupPreset.CreateDefault());
        TemplateName.Focus();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryChangeTemplateAsync(owner)) return;
        var source = _selected;
        LoadEditor(null, source?.Preset?.Clone() ?? VideoDedupPreset.CreateDefault(), source is null ? "" : source.Name + " · 副本");
        TemplateName.Focus();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => SaveConfiguration();

    private bool SaveConfiguration()
    {
        if (_vm is not { } vm || _account is not { } account || !SaveButton.IsEnabled ||
            !ReferenceEquals(account, vm.SelectedAccount?.Model)) return false;
        try
        {
            var preset = HasTemplateChanges() ? PresetEditor.Read() : null;
            var saved = VideoDedupTemplateCatalog.OpenDefault()
                .SaveAccountConfiguration(account.Id, _selected?.Id, TemplateName.Text, preset);
            ReloadTemplates(saved.Id);
            if (_snapshot is not null)
                FeedbackText.Text = $"已保存配置。「{account.DisplayName}」默认使用「{saved.Name}」。";
            return true;
        }
        catch (Exception ex)
        {
            FeedbackText.Text = $"保存失败：{ex.Message}";
            return false;
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || _selected is not { } entry || entry.Id == VideoDedupTemplateCatalog.SystemId ||
            TopLevel.GetTopLevel(this) is not Window owner || !await TryChangeTemplateAsync(owner)) return;
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
