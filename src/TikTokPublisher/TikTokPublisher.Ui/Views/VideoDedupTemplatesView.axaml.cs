using System.ComponentModel;
using System.Text.Json;
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
    private TemplateChoice? _choice;
    private string _initialFingerprint = "";
    private string _initialName = "";
    private bool _settingPicker;
    private bool _confirmingChanges;

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
        WorkspaceForm.IsEnabled = _account is not null;
        LoadTemplates();
    }

    public Task<bool> TryLeaveAsync(Window owner) => ConfirmChangesAsync(owner, includeAccountBinding: true);

    private string CurrentBindingId
    {
        get
        {
            var id = _account?.VideoDedupTemplateId;
            if (!string.IsNullOrWhiteSpace(id))
                return id;
            return _account?.VideoDedupPreset is null ? VideoDedupTemplateStore.SystemId : "";
        }
    }

    private bool HasPendingBinding => _account is not null &&
        _choice is { Id: { } id, Unavailable: false } && id != CurrentBindingId;

    private string DraftName => TemplateNameBox.Text?.Trim() ?? "";

    private bool HasTemplateEdits() => _choice is { CanEdit: true } &&
        (DraftName != _initialName || PresetEditor.CaptureDraft().Fingerprint() != _initialFingerprint);

    private void LoadTemplates(string? preferredId = null, TemplateChoice? newTemplate = null)
    {
        var choices = new List<TemplateChoice>
        {
            new(VideoDedupTemplateStore.SystemId, "系统默认", VideoDedupPreset.CreateDefault()),
        };
        FeedbackText.Text = "";
        if (_account is not null)
        {
            try
            {
                choices.AddRange(VideoDedupTemplateStore.Load()
                    .OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(template => new TemplateChoice(template.Id, template.Name, template.Preset)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                FeedbackText.Text = $"读取视频去重模板失败：{ex.Message}";
            }
        }

        var bound = choices.FirstOrDefault(choice => choice.Id == CurrentBindingId);
        if (bound is null && _account is not null)
        {
            var name = string.IsNullOrWhiteSpace(CurrentBindingId) ? "原账号参数" : "原模板不可用";
            bound = new(CurrentBindingId, name, _account.VideoDedupPreset ?? VideoDedupPreset.CreateDefault(),
                Unavailable: true);
            choices.Add(bound);
        }
        if (newTemplate is not null)
            choices.Add(newTemplate);

        var selected = newTemplate ?? choices.FirstOrDefault(choice => choice.Id == preferredId) ?? bound ?? choices[0];
        _settingPicker = true;
        try
        {
            TemplatePicker.ItemsSource = choices;
            TemplatePicker.SelectedItem = selected;
        }
        finally
        {
            _settingPicker = false;
        }
        DisplayTemplate(selected, bound?.Name ?? "系统默认");
    }

    private void DisplayTemplate(TemplateChoice choice, string boundName)
    {
        _choice = choice;
        AccountSummary.Text = _account is null ? "请选择 TikTok 账号"
            : $"当前账号 · {_account.DisplayName}    已应用 · {boundName}";
        PresetEditor.Load(choice.Preset);
        PresetEditor.SetReadOnly(!choice.CanEdit);
        TemplateNameBox.Text = choice.Name;
        TemplateNameBox.IsReadOnly = choice.Id is not null;
        _initialName = DraftName;
        _initialFingerprint = PresetEditor.CaptureDraft().Fingerprint();

        var users = AccountsUsing(choice.Id);
        TemplateSummary.Text = choice.Unavailable
            ? _account?.VideoDedupPreset is null
                ? "原模板不可用，当前使用系统默认方案。"
                : "这里显示账号保存的去重参数，可新建模板后应用。"
            : choice.Id == VideoDedupTemplateStore.SystemId
                ? "系统默认方案可查看，也可以此方案为基础新建模板。"
                : choice.Id is not null && users.Length > 0
                    ? $"正在使用 · {string.Join("、", users)}" : "";
        ApplyTemplateButton.IsEnabled = HasPendingBinding;
        RenameTemplateButton.IsVisible = choice.IsSavedCustom;
        DeleteTemplateButton.IsVisible = choice.IsSavedCustom;
        DeleteTemplateButton.IsEnabled = users.Length == 0;
        ToolTip.SetTip(DeleteTemplateButton, users.Length > 0 ? "使用此模板的账号换绑后可删除" : null);
        SaveTemplateButton.IsEnabled = choice.CanEdit;
    }

    private string[] AccountsUsing(string? id) => _vm is null || id is null || id == VideoDedupTemplateStore.SystemId
        ? []
        : _vm.Accounts.Where(item => item.Model.VideoDedupTemplateId == id).Select(item => item.DisplayName).ToArray();

    private async Task<bool> ConfirmChangesAsync(Window owner, bool includeAccountBinding)
    {
        var saveTemplate = HasTemplateEdits();
        var applyBinding = includeAccountBinding && HasPendingBinding;
        if (!saveTemplate && !applyBinding)
            return true;
        if (_confirmingChanges)
            return false;

        _confirmingChanges = true;
        var account = _account;
        try
        {
            var changes = saveTemplate && applyBinding ? "模板参数和当前账号的方案选择"
                : saveTemplate ? "模板修改" : "当前账号的方案选择";
            var save = await ConfirmDialog.ShowSaveChangesAsync(owner, "保存视频去重方案",
                $"{changes}尚未保存。是否保存？");
            if (save is null || !ReferenceEquals(account, _account))
                return false;
            if (save is false)
            {
                if (includeAccountBinding)
                    LoadTemplates();
                return true;
            }

            return (!saveTemplate || await SaveTemplateAsync(owner)) && (!applyBinding || ApplyBinding());
        }
        finally
        {
            _confirmingChanges = false;
        }
    }

    private async void OnTemplateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingPicker || _account is null || TemplatePicker.SelectedItem is not TemplateChoice requested ||
            ReferenceEquals(requested, _choice))
            return;

        // Keep the picker aligned with the visible draft until the user decides what to save.
        _settingPicker = true;
        try { TemplatePicker.SelectedItem = _choice; }
        finally { _settingPicker = false; }
        if (TopLevel.GetTopLevel(this) is Window owner && await ConfirmChangesAsync(owner, includeAccountBinding: false))
            LoadTemplates(requested.Id);
    }

    private async void OnNewTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (_account is null || TopLevel.GetTopLevel(this) is not Window owner ||
            !await ConfirmChangesAsync(owner, includeAccountBinding: false))
            return;

        LoadTemplates(newTemplate: new(null, "", _choice?.Preset ?? VideoDedupPreset.CreateDefault()));
        TemplateNameBox.Focus();
    }

    private void OnRenameTemplateClick(object? sender, RoutedEventArgs e)
    {
        TemplateNameBox.IsReadOnly = false;
        TemplateNameBox.Focus();
        TemplateNameBox.SelectAll();
    }

    private async void OnSaveTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            await SaveTemplateAsync(owner);
    }

    private async Task<bool> SaveTemplateAsync(Window owner)
    {
        if (_choice is not { CanEdit: true } choice)
            return false;

        var account = _account;
        SaveTemplateButton.IsEnabled = false;
        try
        {
            var draft = PresetEditor.Read();
            draft.Name = DraftName;
            var users = AccountsUsing(choice.Id);
            if (users.Length > 0 && draft.Fingerprint() != choice.Preset.Fingerprint() &&
                !await ConfirmDialog.ShowAsync(owner, "更新共享去重模板",
                    $"以下账号下次启动队列时会使用新参数：\n{string.Join("、", users)}", width: 520, height: 220))
                return false;
            if (!ReferenceEquals(account, _account) || !ReferenceEquals(choice, _choice))
                return false;

            var saved = VideoDedupTemplateStore.Save(choice.Id, draft);
            LoadTemplates(saved.Id);
            FeedbackText.Text = $"已保存模板「{saved.Name}」。"
                + (HasPendingBinding ? "可以将此模板应用到当前账号。" : "");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            FeedbackText.Text = ex.Message;
            return false;
        }
        finally
        {
            SaveTemplateButton.IsEnabled = _choice?.CanEdit == true;
        }
    }

    private void OnApplyTemplateClick(object? sender, RoutedEventArgs e) => ApplyBinding();

    private bool ApplyBinding()
    {
        if (_vm is null || _account is not { } account || !HasPendingBinding || _choice?.Id is not { } id)
            return false;
        if (HasTemplateEdits())
        {
            FeedbackText.Text = "请先保存模板参数，再应用到当前账号。";
            return false;
        }

        var previousId = account.VideoDedupTemplateId;
        var previousPreset = account.VideoDedupPreset;
        try
        {
            var template = id == VideoDedupTemplateStore.SystemId ? null
                : VideoDedupTemplateStore.Load().FirstOrDefault(entry => entry.Id == id)
                    ?? throw new InvalidOperationException("所选模板已不存在，请重新选择。");
            account.VideoDedupTemplateId = id;
            account.VideoDedupPreset = template?.Preset.Clone();
            _vm.SaveAccountProfile(account);
            LoadTemplates();
            FeedbackText.Text = $"已将「{_choice?.Name}」应用到「{account.DisplayName}」。";
            return true;
        }
        catch (Exception ex)
        {
            account.VideoDedupTemplateId = previousId;
            account.VideoDedupPreset = previousPreset;
            FeedbackText.Text = $"应用模板失败：{ex.Message}";
            return false;
        }
    }

    private async void OnDeleteTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _choice is not { IsSavedCustom: true, Id: { } id } choice ||
            TopLevel.GetTopLevel(this) is not Window owner ||
            !await ConfirmDialog.ShowAsync(owner, "删除视频去重模板", $"确认删除「{choice.Name}」？") ||
            !ReferenceEquals(choice, _choice))
            return;
        try
        {
            VideoDedupTemplateStore.Delete(id, _vm.Accounts.Select(item => item.Model));
            LoadTemplates();
            FeedbackText.Text = $"已删除模板「{choice.Name}」。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            FeedbackText.Text = ex.Message;
        }
    }

    private sealed record TemplateChoice(string? Id, string Name, VideoDedupPreset Preset, bool Unavailable = false)
    {
        public bool IsSavedCustom => Id is not null and not VideoDedupTemplateStore.SystemId && !Unavailable;
        public bool CanEdit => Id is null || IsSavedCustom;
        public override string ToString() => Id is null ? "新模板（未保存）" : Name;
    }
}
