using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.Views;

internal sealed record VideoDedupTemplateEditResult(string Id, bool Deleted = false);

internal sealed class VideoDedupTemplateEditorWindow : Window
{
    private readonly TextBox _name = new() { Watermark = "模板名称" };
    private readonly VideoDedupPresetEditor _editor = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public VideoDedupTemplateEditorWindow(
        VideoDedupTemplate? existing,
        VideoDedupPreset initial,
        string name,
        Func<IReadOnlyList<TikTokAccountProfile>>? accounts = null,
        bool readOnly = false)
    {
        Title = readOnly ? "系统默认去重模板" : existing is null ? "新建视频去重模板" : "编辑视频去重模板";
        Width = 840;
        Height = 720;
        MinWidth = 460;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name.Text = name;
        _name.IsReadOnly = readOnly || existing is not null;
        AutomationProperties.SetName(_name, "模板名称");
        _editor.Load(initial);
        if (readOnly)
            _editor.SetReadOnly();

        var header = new StackPanel { Spacing = 5 };
        header.Children.Add(new TextBlock { Text = "模板名称" });
        var nameRow = new DockPanel();
        header.Children.Add(nameRow);

        int CountUsers(string id) => accounts?.Invoke().Count(account => account.VideoDedupTemplateId == id) ?? 0;
        if (existing is not null)
        {
            var templateAccounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            var users = CountUsers(existing.Id);
            var rename = new Button { Content = "重命名" };
            var delete = new Button { Content = "删除模板", Classes = { "dangerAction" }, IsEnabled = users == 0 };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(8, 0, 0, 0),
                Children = { rename, delete },
            };
            DockPanel.SetDock(actions, Dock.Right);
            nameRow.Children.Add(actions);
            header.Children.Add(new TextBlock
            {
                Text = users > 0 ? $"{users} 个账号正在使用。全部换绑后可删除。" : "当前没有账号使用此模板。",
                Classes = { "pageSummary" },
                TextWrapping = TextWrapping.Wrap,
            });

            rename.Click += async (_, _) =>
            {
                var newName = await TextPromptDialog.ShowAsync(this, "重命名视频去重模板", "请输入模板名称", _name.Text ?? "");
                if (string.IsNullOrWhiteSpace(newName) || newName == _name.Text)
                    return;
                try
                {
                    VideoDedupTemplateStore.Rename(existing.Id, newName.Trim());
                    _name.Text = newName.Trim();
                    _error.Text = "";
                }
                catch (Exception ex)
                {
                    _error.Text = ex.Message;
                }
            };

            delete.Click += async (_, _) =>
            {
                if (!await ConfirmDialog.ShowAsync(this, "删除视频去重模板", $"确认删除「{_name.Text}」？"))
                    return;
                try
                {
                    VideoDedupTemplateStore.Delete(existing.Id, templateAccounts());
                    Close(new VideoDedupTemplateEditResult(VideoDedupTemplateStore.SystemId, Deleted: true));
                }
                catch (Exception ex)
                {
                    _error.Text = ex.Message;
                }
            };
        }
        else if (readOnly)
        {
            header.Children.Add(new TextBlock
            {
                Text = "可通过发布配置中的「新建模板」自定义参数。",
                Classes = { "pageSummary" },
            });
        }
        nameRow.Children.Add(_name);

        var close = new Button { Content = readOnly ? "关闭" : "取消", IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { close },
        };
        if (!readOnly)
        {
            var save = new Button { Content = "保存模板", Classes = { "primaryAction" } };
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
                    var users = existing is null ? 0 : CountUsers(existing.Id);
                    if (existing is not null && users > 0 && existing.Preset.Fingerprint() != draft.Fingerprint() &&
                        !await ConfirmDialog.ShowAsync(this, "更新共享模板",
                            $"有 {users} 个账号使用此模板。保存后，这些账号下次启动队列时将使用新参数。"))
                        return;
                    var saved = VideoDedupTemplateStore.Save(existing?.Id, draft);
                    Close(new VideoDedupTemplateEditResult(saved.Id));
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
            buttons.Children.Add(save);
        }

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        root.Children.Add(header);
        Grid.SetRow(_editor, 1);
        root.Children.Add(_editor);
        var footer = new StackPanel { Spacing = 8, Children = { _error, buttons } };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }
}
