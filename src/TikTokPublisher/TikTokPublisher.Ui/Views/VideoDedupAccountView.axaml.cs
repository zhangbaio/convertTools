using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class VideoDedupAccountView : UserControl
{
    private MainViewModel? _vm;
    private VideoDedupTemplateAccount? _account;
    private string _accountName = "";
    private VideoDedupTemplateSnapshot? _snapshot;
    private VideoDedupTemplateEntry? _template;
    private string? _selectionId;
    private string _initialFingerprint = "";
    private string _initialName = "";
    private bool _confirmingChanges;
    private bool _loadingSelection;

    public VideoDedupAccountView()
    {
        InitializeComponent();
        LoadAccount(null, null);
    }

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
        var profile = _vm?.SelectedAccount?.Model;
        LoadAccount(profile is null ? null : VideoDedupTemplateAccount.ForTikTok(profile), profile?.DisplayName);
    }

    public void LoadAccount(VideoDedupTemplateAccount? account, string? name)
    {
        _account = account;
        _accountName = name ?? "";
        ReloadTemplates();
    }

    private void ReloadTemplates(string? selectionId = null)
    {
        FeedbackText.Text = "";
        if (_account is not { } account)
        {
            AccountSummary.Text = "请先选择账号";
            _snapshot = null;
            LoadEditor(null, null);
            return;
        }

        try
        {
            _snapshot = VideoDedupTemplateCatalog.OpenDefault().Load();
            var selected = _snapshot.SelectedTemplateId(account);
            var current = _snapshot.DefaultFor(account);
            var choice = selected is null ? $"跟随默认 · {current.Name}" : $"手动指定 · {current.Name}";
            AccountSummary.Text = $"当前账号 · {_accountName}　{choice}";
            LoadEditor(selectionId ?? selected ?? VideoDedupTemplateCatalog.FollowDefaultId, account);
        }
        catch (Exception ex)
        {
            _snapshot = null;
            LoadEditor(null, null);
            AccountSummary.Text = "视频去重模板库读取失败";
            FeedbackText.Text = ex.Message;
        }
    }

    private void LoadEditor(string? selectionId, VideoDedupTemplateAccount? account,
        VideoDedupPreset? draft = null, string? draftName = null)
    {
        var defaultTemplate = account is { } target
            ? _snapshot?.Templates.FirstOrDefault(entry => entry.Id == target.DefaultTemplateId)
            : null;
        _template = selectionId == VideoDedupTemplateCatalog.FollowDefaultId
            ? defaultTemplate
            : _snapshot?.Templates.FirstOrDefault(entry => entry.Id == selectionId);
        if (selectionId is not null && _template is null && defaultTemplate is not null)
        {
            selectionId = VideoDedupTemplateCatalog.FollowDefaultId;
            _template = defaultTemplate;
        }
        _selectionId = selectionId;
        _loadingSelection = true;
        try
        {
            var items = new List<ComboBoxItem>();
            if (_snapshot is not null && defaultTemplate is not null)
            {
                items.Add(new ComboBoxItem { Content = $"跟随默认（{defaultTemplate.Name}）", Tag = VideoDedupTemplateCatalog.FollowDefaultId });
                items.AddRange(_snapshot.Templates.Select(entry => new ComboBoxItem { Content = entry.Name, Tag = entry.Id }));
            }
            TemplateSelector.ItemsSource = items;
            SelectEditorItem();
        }
        finally { _loadingSelection = false; }

        SaveButton.IsEnabled = _snapshot is not null && account is not null;
        TemplateSelector.IsEnabled = NewButton.IsEnabled = SaveButton.IsEnabled;
        PresetEditor.IsEnabled = SaveButton.IsEnabled;
        var builtIn = VideoDedupTemplateCatalog.IsBuiltIn(_template?.Id);
        TemplateName.IsEnabled = SaveButton.IsEnabled && !builtIn;
        CopyButton.IsEnabled = _template is not null;
        DeleteButton.IsEnabled = _template is not null && !builtIn;
        TemplateName.Text = draftName ?? _template?.Name ?? "";
        PresetEditor.Load(draft ?? _template?.Preset);
        _initialName = TemplateName.Text ?? "";
        _initialFingerprint = PresetEditor.CaptureDraft().Fingerprint();

        var count = _template is null || _snapshot is null ? 0 :
            _snapshot.AccountDefaults.Values.Count(id => id == _template.Id);
        UsageSummary.Text = count == 0 ? "" : $"已为 {count} 个账号手动指定";
    }

    private void SelectEditorItem()
    {
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        try
        {
            TemplateSelector.SelectedItem = TemplateSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, _selectionId));
        }
        finally { _loadingSelection = wasLoading; }
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
            if (save is null || account != _account) return false;
            if (save == true) return SaveConfiguration();
            ReloadTemplates(discardSelection ? null : _selectionId);
            return true;
        }
        finally { _confirmingChanges = false; }
    }

    private bool HasUnsavedChanges() => SaveButton.IsEnabled && _account is { } account &&
        (HasTemplateChanges() || _selectionId != (_snapshot?.SelectedTemplateId(account) ?? VideoDedupTemplateCatalog.FollowDefaultId));

    private bool HasTemplateChanges() => PresetEditor.IsEnabled &&
        (_selectionId is null || (TemplateName.Text?.Trim() ?? "") != _initialName.Trim() ||
         PresetEditor.CaptureDraft().Fingerprint() != _initialFingerprint);

    private async void OnTemplateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection || (TemplateSelector.SelectedItem as ComboBoxItem)?.Tag is not string id || id == _selectionId)
            return;
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryChangeTemplateAsync(owner))
        {
            SelectEditorItem();
            return;
        }
        ReloadTemplates(id);
    }

    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryChangeTemplateAsync(owner)) return;
        LoadEditor(null, _account, VideoDedupPreset.CreateDefault());
        TemplateName.Focus();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryChangeTemplateAsync(owner)) return;
        if (_template is not { } source) return;
        LoadEditor(null, _account, source.Preset.Clone(), source.Name + " · 副本");
        TemplateName.Focus();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => SaveConfiguration();

    private bool SaveConfiguration()
    {
        if (_account is not { } account || !SaveButton.IsEnabled) return false;
        if (_vm is not null && (_vm.SelectedAccount?.Model is not { } profile ||
            VideoDedupTemplateAccount.ForTikTok(profile) != account)) return false;

        try
        {
            var preset = HasTemplateChanges() ? PresetEditor.Read() : null;
            var saved = VideoDedupTemplateCatalog.OpenDefault()
                .SaveAccountConfiguration(account, _selectionId, TemplateName.Text, preset);
            ReloadTemplates();
            if (_snapshot is not null)
                FeedbackText.Text = $"已保存配置。「{_accountName}」使用「{saved.Name}」。";
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
        if (_account is not { } account || _template is not { } entry || VideoDedupTemplateCatalog.IsBuiltIn(entry.Id) ||
            TopLevel.GetTopLevel(this) is not Window owner || !await TryChangeTemplateAsync(owner)) return;

        try
        {
            var catalog = VideoDedupTemplateCatalog.OpenDefault();
            var snapshot = catalog.Load();
            var bound = snapshot.AccountDefaults.Values.Count(id => id == entry.Id);
            string? replacement = null;
            if (bound > 0)
            {
                replacement = await VideoDedupTemplatePicker.SelectReplacementAsync(owner, "选择替代模板",
                    $"有 {bound} 个账号正在使用「{entry.Name}」。请选择替代模板，或恢复各账号的默认规则。",
                    snapshot.Templates.Where(item => item.Id != entry.Id).ToArray());
                if (replacement is null) return;
            }
            else if (!await ConfirmDialog.ShowAsync(owner, "删除去重模板", $"确认删除「{entry.Name}」？"))
                return;

            if (_account != account) return;
            catalog.Delete(entry.Id, replacement);
            ReloadTemplates();
            FeedbackText.Text = "去重模板已删除。";
        }
        catch (Exception ex) { FeedbackText.Text = $"删除模板失败：{ex.Message}"; }
    }
}
