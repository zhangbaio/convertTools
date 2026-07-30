using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class ArchivedProjectsView : UserControl
{
    private ScrollViewer? _archivedListScrollViewer;

    public ArchivedProjectsView()
    {
        InitializeComponent();
        ArchivedList.Loaded += (_, _) => AttachArchivedListScrollViewer(retryAfterLayout: true);
        ArchivedList.SizeChanged += (_, _) => SyncArchivedHeaderScroll();
        Unloaded += (_, _) => DetachArchivedListScrollViewer();
        Loaded += (_, _) =>
        {
            if (Vm is { Rows.Count: 0 } vm && !string.IsNullOrWhiteSpace(vm.WorkspacePath))
                vm.RefreshCommand.Execute(null);
        };
    }

    public void Bind(ArchivedProjectsViewModel vm) => DataContext = vm;

    private ArchivedProjectsViewModel? Vm => DataContext as ArchivedProjectsViewModel;

    private void AttachArchivedListScrollViewer(bool retryAfterLayout)
    {
        var scrollViewer = ArchivedList
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is null)
        {
            if (retryAfterLayout)
            {
                Dispatcher.UIThread.Post(
                    () => AttachArchivedListScrollViewer(retryAfterLayout: false),
                    DispatcherPriority.Loaded);
            }
            return;
        }

        if (ReferenceEquals(_archivedListScrollViewer, scrollViewer))
        {
            SyncArchivedHeaderScroll();
            return;
        }

        DetachArchivedListScrollViewer();
        _archivedListScrollViewer = scrollViewer;
        _archivedListScrollViewer.ScrollChanged += OnArchivedListScrollChanged;
        SyncArchivedHeaderScroll();
    }

    private void DetachArchivedListScrollViewer()
    {
        if (_archivedListScrollViewer is not null)
            _archivedListScrollViewer.ScrollChanged -= OnArchivedListScrollChanged;
        _archivedListScrollViewer = null;
    }

    private void OnArchivedListScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        SyncArchivedHeaderScroll();

    private void SyncArchivedHeaderScroll()
    {
        var scrollViewer = _archivedListScrollViewer;
        if (scrollViewer is null)
            return;

        if (scrollViewer.Viewport.Width > 0)
        {
            var verticalScrollbarWidth = Math.Max(0, ArchivedList.Bounds.Width - scrollViewer.Viewport.Width);
            if (Math.Abs(ArchivedHeaderScrollViewer.Margin.Right - verticalScrollbarWidth) > 0.5)
            {
                ArchivedHeaderScrollViewer.Margin = new Thickness(
                    0,
                    0,
                    verticalScrollbarWidth,
                    0);
            }
        }

        ArchivedHeaderScrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);
    }

    private async void OnChooseArchiveRootClick(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (vm is null || storage is null) return;

        var start = string.IsNullOrWhiteSpace(vm.ArchiveRootDir)
            ? null
            : await storage.TryGetFolderFromPathAsync(vm.ArchiveRootDir);
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择归档目录",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        vm.SetArchiveRootDir(folder.Path.LocalPath);
    }

    private void OnOpenArchiveDirClick(object? sender, RoutedEventArgs e) => Vm?.OpenArchiveRoot();

    private void OnOpenSourceClick(object? sender, RoutedEventArgs e) => Vm?.OpenArchiveSourceRoot();

    private void OnOpenWorkflowClick(object? sender, RoutedEventArgs e) => Vm?.OpenArchiveWorkflowRoot();

    private void OnSelectToCurrentClick(object? sender, RoutedEventArgs e) => Vm?.SelectToCurrentProject();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => Vm?.SelectAll();

    private void OnClearSelectionClick(object? sender, RoutedEventArgs e) => Vm?.ClearSelection();

    private async void OnRestoreSelectedClick(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (vm is null || owner is null) return;
        var count = vm.GetActionTargetCount();
        if (count == 0)
        {
            vm.StatusMessage = "请先勾选要回退的归档项目（或点击选中一行）";
            return;
        }

        if (!await ConfirmAsync(
                owner,
                "确认回退归档项目",
                $"确认将 {count} 个归档项目回退到原工作区？若原位置已有同名目录，将跳过并报错，不会覆盖。"))
            return;
        await vm.RestoreSelectedAsync();
    }

    private async void OnDeleteSelectedClick(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (vm is null || owner is null) return;
        var count = vm.GetActionTargetCount();
        if (count == 0)
        {
            vm.StatusMessage = "请先勾选要删除的归档项目（或点击选中一行）";
            return;
        }

        if (!await ConfirmAsync(
                owner,
                "确认删除归档项目",
                $"确认删除 {count} 个归档项目？此操作不可恢复。"))
            return;
        await vm.DeleteSelectedAsync();
    }

    private async void OnMigrateLegacyArchiveClick(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (vm is null || owner is null || vm.IsMigratingLegacyArchive)
            return;

        var preview = await vm.PrepareLegacyArchiveMigrationAsync();
        if (preview is null)
            return;

        if (preview.MigratableCount == 0)
        {
            var details = preview.Notes.Count == 0
                ? "旧全局归档中没有能明确归属于当前账号的项目；未确认归属的记录均已保留在原目录。"
                : string.Join(Environment.NewLine, preview.Notes.Take(8));
            await ShowMessageAsync(owner, "没有可迁移项目", details);
            return;
        }

        var message =
            $"旧全局归档：{preview.SourceArchiveRoot}{Environment.NewLine}" +
            $"当前账号归档：{preview.TargetArchiveRoot}{Environment.NewLine}{Environment.NewLine}" +
            $"可安全迁移：{preview.MigratableCount} 个{Environment.NewLine}" +
            $"归属不明确（保留原处）：{preview.SkippedOwnershipCount} 个{Environment.NewLine}" +
            $"路径冲突或文件缺失（保留原处）：{preview.ConflictCount} 个{Environment.NewLine}{Environment.NewLine}" +
            "迁移会先复制并校验，再删除旧副本；不会覆盖目标目录中的同名项目。确认继续吗？";
        if (!await ConfirmAsync(owner, "确认迁移当前账号旧归档", message, height: 360))
            return;

        var result = await vm.MigrateLegacyArchiveAsync(preview);
        if (result is null)
            return;

        var resultMessage =
            $"成功迁移：{result.MigratedCount} 个{Environment.NewLine}" +
            $"跳过并保留原处：{result.SkippedCount} 个{Environment.NewLine}" +
            $"失败：{result.FailedCount} 个{Environment.NewLine}{Environment.NewLine}" +
            $"当前账号归档目录已切换为：{result.TargetArchiveRoot}";
        if (result.Messages.Count > 0)
            resultMessage += Environment.NewLine + string.Join(Environment.NewLine, result.Messages.Take(5));
        await ShowMessageAsync(owner, "旧归档迁移完成", resultMessage);
    }

    private async void OnSyncSelectedClick(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        await vm.SyncCheckedToManagementAsync();
    }

    private void OnOpenRowSourceClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = (sender as Control)?.DataContext as ArchivedProjectRowViewModel;
        if (row is not null && Vm is not null)
        {
            Vm.SelectedRow = row;
            Vm.OpenRowSource(row);
        }
    }

    private void OnOpenRowWorkflowClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = (sender as Control)?.DataContext as ArchivedProjectRowViewModel;
        if (row is not null && Vm is not null)
        {
            Vm.SelectedRow = row;
            Vm.OpenRowWorkflow(row);
        }
    }

    private static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        double height = 190)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var cancelButton = BuildDialogButton("取消", () => dialog.Close(false));
        var okButton = BuildDialogButton("继续", () => dialog.Close(true), primary: true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton },
                },
            },
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    private static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var okButton = BuildDialogButton("确定", () => dialog.Close(), primary: true);
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { okButton },
        };
        Grid.SetRow(buttonPanel, 1);
        dialog.Content = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                },
                buttonPanel,
            },
        };
        await dialog.ShowDialog(owner);
    }

    private static Button BuildDialogButton(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 84,
        };
        if (primary)
            button.Classes.Add("primaryAction");
        button.Click += (_, _) => click();
        return button;
    }
}
