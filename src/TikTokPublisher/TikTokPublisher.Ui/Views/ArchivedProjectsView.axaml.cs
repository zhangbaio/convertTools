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

    private static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 190,
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
