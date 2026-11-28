using System.ComponentModel;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class AnalyticsView : Avalonia.Controls.UserControl
{
    private AnalyticsViewModel? _viewModel;

    public AnalyticsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as AnalyticsViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            RefreshOverview();
        };
        DailyChart.HoverTextChanged += text => HoverText.Text = text ?? string.Empty;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible && DataContext is AnalyticsViewModel vm)
                _ = vm.ActivateAsync();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalyticsViewModel.OverviewSeries))
            RefreshOverview();
    }

    private void RefreshOverview()
    {
        if (_viewModel is not { } vm) return;
        HoverText.Text = "";
        var count = vm.OverviewSeries.Count(account => account.Series.Points.Any(point => double.IsFinite(point.Value)));
        DashboardSummary.Update(vm.OverviewSeries, ("区间有数据账号", $"{count}/{vm.OverviewSeries.Count}"));
        AccountRanking.Update(vm.OverviewSeries, vm.SelectedKey, key => vm.SelectTabCommand.Execute(key));
    }
}
