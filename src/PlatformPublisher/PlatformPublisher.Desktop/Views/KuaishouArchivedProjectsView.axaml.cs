using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouArchivedProjectsView : UserControl
{
    public event EventHandler? ReturnRequested;

    public KuaishouArchivedProjectsView()
    {
        InitializeComponent();
    }

    public void Refresh()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            ReturnButton.Content = viewModel.KuaishouArchiveIsPersonal ? "返回快手分账个人" : "返回快手分账企业";
            UpdateEditionTabs(viewModel.KuaishouArchiveIsPersonal);
            viewModel.RefreshKuaishouArchivedProjects();
        }
    }

    private void OnPersonalTabClick(object? sender, RoutedEventArgs e) => SelectEdition(personal: true);

    private void OnEnterpriseTabClick(object? sender, RoutedEventArgs e) => SelectEdition(personal: false);

    private void SelectEdition(bool personal)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;
        viewModel.SelectKuaishouArchiveEdition(personal);
        UpdateEditionTabs(personal);
        ReturnButton.Content = personal ? "返回快手分账个人" : "返回快手分账企业";
    }

    private void UpdateEditionTabs(bool personal)
    {
        SetTabActive(PersonalTab, personal);
        SetTabActive(EnterpriseTab, !personal);
    }

    private static void SetTabActive(Button button, bool active)
    {
        if (active)
            button.Classes.Add("navActive");
        else
            button.Classes.Remove("navActive");
    }

    private async void OnPickArchiveRootClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = viewModel.KuaishouArchiveIsPersonal ? "选择个人版归档目录" : "选择企业版归档目录",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
            return;
        await viewModel.SaveKuaishouArchiveRootAsync(folders[0].Path.LocalPath);
    }

    private void OnReturnClick(object? sender, RoutedEventArgs e) =>
        ReturnRequested?.Invoke(this, EventArgs.Empty);

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => Refresh();

    private async void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.SelectedArchivedProject is not { } row)
        {
            if (DataContext is MainWindowViewModel empty)
                empty.StatusMessage = "请先选择要回退的归档项目。";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
            return;
        var dialog = new Window
        {
            Title = "确认回退归档项目",
            Width = 460,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"确认将《{row.DisplayName}》回退到原工作区？原位置已有同名目录时不会覆盖。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                    },
                },
            },
        };
        var buttons = (StackPanel)((StackPanel)dialog.Content).Children[1];
        var confirmed = false;
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = "回退" };
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        await dialog.ShowDialog(owner);
        if (confirmed)
            await viewModel.RestoreSelectedKuaishouArchiveAsync();
    }
}
