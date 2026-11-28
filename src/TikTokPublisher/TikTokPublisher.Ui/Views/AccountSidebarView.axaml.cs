using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class AccountSidebarView : UserControl
{
    private static readonly DataFormat<string> AccountDragFormat = DataFormat.CreateStringApplicationFormat("tiktok-account-reorder");
    public Func<Task<bool>>? CanChangeAccountAsync { get; set; }
    private bool _changingAccount;
    private AccountItemViewModel? _pressedAccount;
    private AccountItemViewModel? _draggedAccount;
    private Point _dragStart;
    private ListBoxItem? _dropRow;

    public AccountSidebarView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(AccountList, true);
        AccountList.AddHandler(DragDrop.DragOverEvent, OnAccountDragOver);
        AccountList.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropIndicator());
        AccountList.AddHandler(DragDrop.DropEvent, OnAccountDrop);
    }

    private void OnDragGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_changingAccount || _draggedAccount is not null ||
            sender is not Border { DataContext: AccountItemViewModel account } grip ||
            !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            return;

        _pressedAccount = account;
        _dragStart = e.GetPosition(AccountList);
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private async void OnDragGripMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedAccount is not { } account || sender is not Border grip ||
            !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            return;
        var delta = e.GetPosition(AccountList) - _dragStart;
        if (Math.Abs(delta.X) < 5 && Math.Abs(delta.Y) < 5)
            return;

        _pressedAccount = null;
        _draggedAccount = account;
        e.Pointer.Capture(null);
        e.Handled = true;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(AccountDragFormat, "move"));
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            if (DataContext is MainViewModel vm)
                vm.StatusMessage = $"无法拖动账号：{ex.Message}";
        }
        finally
        {
            _draggedAccount = null;
            ClearDropIndicator();
        }
    }

    private void OnDragGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedAccount is null)
            return;
        _pressedAccount = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnDragGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _pressedAccount = null;

    private void OnAccountDragOver(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();
        var row = FindDropRow(e);
        e.DragEffects = row is null ? DragDropEffects.None : DragDropEffects.Move;
        if (row is not null)
        {
            _dropRow = row;
            row.Classes.Add(IsAfter(e, row) ? "accountDropAfter" : "accountDropBefore");
        }
        e.Handled = true;
    }

    private void OnAccountDrop(object? sender, DragEventArgs e)
    {
        var row = FindDropRow(e);
        e.DragEffects = DragDropEffects.None;
        if (DataContext is MainViewModel vm && _draggedAccount is { } account &&
            row is { DataContext: AccountItemViewModel target })
        {
            var after = IsAfter(e, row);
            ClearDropIndicator();
            _changingAccount = true;
            try
            {
                vm.MoveSidebarAccount(account, target, after);
                AccountList.SetCurrentValue(ListBox.SelectedItemProperty, vm.SidebarSelectedAccount);
            }
            finally
            {
                _changingAccount = false;
            }
            e.DragEffects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private ListBoxItem? FindDropRow(DragEventArgs e)
    {
        if (_draggedAccount is null)
            return null;
        return AccountList.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(row =>
            row.DataContext is AccountItemViewModel target &&
            !ReferenceEquals(target, _draggedAccount) &&
            new Rect(row.Bounds.Size).Contains(e.GetPosition(row)));
    }

    private static bool IsAfter(DragEventArgs e, ListBoxItem row) => e.GetPosition(row).Y >= row.Bounds.Height / 2;

    private void ClearDropIndicator()
    {
        _dropRow?.Classes.Remove("accountDropBefore");
        _dropRow?.Classes.Remove("accountDropAfter");
        _dropRow = null;
    }

    private Task<bool> ConfirmAccountChangeAsync() =>
        CanChangeAccountAsync?.Invoke() ?? Task.FromResult(true);

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && await ConfirmAccountChangeAsync())
            vm.AddAccountCommand.Execute(null);
    }

    internal Services.TikTok.TikTokPublishAccountBatchRunner? PublishAccountRunner { get; set; }

    private async void OnBatchAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this) is not Window owner ||
            !await ConfirmAccountChangeAsync())
            return;

        try
        {
            var dialog = new TikTokAccountBatchImportDialog();
            dialog.Bind(vm.CreateAccountBatchImporter(), vm.LoadVideoDedupTemplates(),
                vm.Accounts.Select(account => account.Model).ToArray(), vm.ApplyAccountBatchImport, PublishAccountRunner);
            await dialog.ShowDialog(owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            vm.StatusMessage = "无法读取去重模板库，请检查后再批量添加账号。";
        }
    }

    private async void OnAccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingAccount || DataContext is not MainViewModel vm ||
            AccountList.SelectedItem is not AccountItemViewModel account ||
            ReferenceEquals(account, vm.SelectedAccount))
            return;

        _changingAccount = true;
        try
        {
            if (await ConfirmAccountChangeAsync())
                vm.SidebarSelectedAccount = account;
            else
                AccountList.SetCurrentValue(ListBox.SelectedItemProperty, vm.SidebarSelectedAccount);
        }
        finally
        {
            _changingAccount = false;
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedAccount is null)
            return;

        if (vm.Accounts.Count <= 1)
        {
            vm.StatusMessage = "至少需要保留一个 TikTok 账号";
            return;
        }
        if (!await ConfirmAccountChangeAsync())
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "删除账号",
            $"确认删除账号「{vm.SelectedAccount.DisplayName}」？");
        if (!confirmed)
            return;

        vm.RemoveSelectedAccount();
    }
}
