using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.Views;

internal sealed class VideoDedupTemplateEditorWindow : Window
{
    private readonly TextBox _name = new() { Watermark = "模板名称" };
    private readonly VideoDedupPresetEditor _editor = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public VideoDedupTemplateEditorWindow(VideoDedupTemplate? existing, VideoDedupPreset initial, string name, Func<int> countUsers)
    {
        Title = existing is null ? "新建视频去重模板" : "编辑视频去重模板";
        Width = 840;
        Height = 720;
        MinWidth = 460;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name.Text = name;
        AutomationProperties.SetName(_name, "模板名称");
        _editor.Load(initial);

        var save = new Button { Content = "保存模板", Classes = { "primaryAction" } };
        var cancel = new Button { Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close();
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var draft = _editor.Read();
                draft.Name = _name.Text?.Trim() ?? "";
                if (draft.Name.Length == 0)
                {
                    _error.Text = "请输入模板名称。";
                    return;
                }
                var users = countUsers();
                if (existing is not null && users > 0 && existing.Preset.Fingerprint() != draft.Fingerprint() &&
                    !await ConfirmDialog.ShowAsync(this, "更新共享模板",
                        $"有 {users} 个账号使用此模板。保存后，这些账号下次启动队列时将使用新参数。"))
                    return;
                Close(VideoDedupTemplateStore.Save(existing?.Id, draft));
            }
            catch (Exception ex)
            {
                _error.Text = ex.Message;
            }
            finally
            {
                save.IsEnabled = true;
            }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        root.Children.Add(new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "模板名称" }, _name } });
        Grid.SetRow(_editor, 1);
        root.Children.Add(_editor);
        var footer = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, save },
                },
            },
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }
}

internal sealed class VideoDedupTemplateManagerWindow : Window
{
    private readonly Func<IReadOnlyList<TikTokAccountProfile>> _accounts;
    private readonly string _draftTemplateId;
    private readonly ListBox _templates = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _edit = new() { Content = "编辑" };
    private readonly Button _copy = new() { Content = "复制" };
    private readonly Button _rename = new() { Content = "重命名" };
    private readonly Button _delete = new() { Content = "删除", Classes = { "dangerAction" } };

    public VideoDedupTemplateManagerWindow(Func<IReadOnlyList<TikTokAccountProfile>> accounts, string draftTemplateId)
    {
        _accounts = accounts;
        _draftTemplateId = draftTemplateId;
        Title = "管理视频去重模板";
        Width = 520;
        Height = 460;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var add = new Button { Content = "新建模板", Classes = { "primaryAction" } };
        add.Click += async (_, _) => await EditAsync(null, VideoDedupPreset.CreateDefault(), "");
        _edit.Click += async (_, _) =>
        {
            if (_templates.SelectedItem is TemplateRow row)
                await EditAsync(row.Template, row.Template.Preset, row.Template.Name);
        };
        _copy.Click += async (_, _) =>
        {
            if (_templates.SelectedItem is TemplateRow row)
                await EditAsync(null, row.Template.Preset, row.Template.Name + " 副本");
        };
        _rename.Click += async (_, _) => await RenameAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        _templates.SelectionChanged += (_, _) => UpdateActions();
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        close.Click += (_, _) => Close();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10, Margin = new Thickness(16) };
        root.Children.Add(new WrapPanel { Children = { add, _edit, _copy, _rename, _delete } });
        Grid.SetRow(_templates, 1);
        root.Children.Add(_templates);
        var footer = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(_status);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Refresh(draftTemplateId);
    }

    private int CountUsers(string id) => _accounts().Count(account => account.VideoDedupTemplateId == id);

    private void Refresh(string? selectedId)
    {
        var rows = VideoDedupTemplateStore.Load().OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(template => new TemplateRow(template, CountUsers(template.Id))).ToArray();
        _templates.ItemsSource = rows;
        _templates.SelectedItem = rows.FirstOrDefault(row => row.Template.Id == selectedId) ?? rows.FirstOrDefault();
        UpdateActions();
    }

    private void UpdateActions()
    {
        var row = _templates.SelectedItem as TemplateRow;
        _edit.IsEnabled = _copy.IsEnabled = _rename.IsEnabled = row is not null;
        _delete.IsEnabled = row is { AccountCount: 0 } && row.Template.Id != _draftTemplateId;
        _status.Text = row switch
        {
            null => "暂无自定义模板。系统默认始终可选。",
            { AccountCount: > 0 } => "请先为使用这个模板的账号更换模板，再删除。",
            _ when row.Template.Id == _draftTemplateId => "请先在账号配置中改选其他模板，再删除。",
            _ => "",
        };
    }

    private async Task EditAsync(VideoDedupTemplate? existing, VideoDedupPreset initial, string name)
    {
        try
        {
            var editor = new VideoDedupTemplateEditorWindow(existing, initial, name,
                () => existing is null ? 0 : CountUsers(existing.Id));
            var saved = await editor.ShowDialog<VideoDedupTemplate?>(this);
            if (saved is not null)
                Refresh(saved.Id);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task DeleteAsync()
    {
        if (_templates.SelectedItem is not TemplateRow row ||
            !await ConfirmDialog.ShowAsync(this, "删除视频去重模板", $"确认删除「{row.Template.Name}」？"))
            return;
        try
        {
            VideoDedupTemplateStore.Delete(row.Template.Id, _accounts());
            Refresh(null);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task RenameAsync()
    {
        if (_templates.SelectedItem is not TemplateRow row)
            return;
        var name = await TextPromptDialog.ShowAsync(this, "重命名视频去重模板", "请输入模板名称", row.Template.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == row.Template.Name)
            return;
        try
        {
            VideoDedupTemplateStore.Rename(row.Template.Id, name);
            Refresh(row.Template.Id);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private sealed record TemplateRow(VideoDedupTemplate Template, int AccountCount)
    {
        public override string ToString() => $"{Template.Name} · {AccountCount} 个账号";
    }
}
