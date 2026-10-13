using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class OriginalDramaLookupDialog : Window
{
    private readonly string _excelPath;
    private readonly ClientSettings _settings;
    private readonly TextBox _input;
    private readonly ListBox _results;
    private readonly ObservableCollection<OriginalDramaLookupRow> _resultItems = [];
    private readonly TextBlock _status;
    private readonly Button _query;
    private readonly CancellationTokenSource _cancellation = new();
    private IReadOnlyList<OriginalDramaLookupRow> _rows = [];
    private bool _running;

    private OriginalDramaLookupDialog(string excelPath, ClientSettings settings)
    {
        _excelPath = excelPath;
        _settings = settings;
        Title = "查询原剧";
        Width = 920;
        Height = 680;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,140,Auto,*,Auto"),
            RowSpacing = 10,
        };
        root.Children.Add(new TextBlock
        {
            Text = "每行一个新剧名。按当前账号的上传记录 Excel 查找原剧名，再用红果高码率标准版同时搜索。题材和横竖屏单独列出，查询结果可导出 Excel。",
            TextWrapping = TextWrapping.Wrap,
        });

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "每行输入一个新剧名",
        };
        Grid.SetRow(_input, 1);
        root.Children.Add(_input);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        _query = new Button { Content = "查询", MinWidth = 96 };
        _query.Click += async (_, _) => await QueryAsync();
        actions.Children.Add(_query);
        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            Text = excelPath,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(_status, 1);
        actions.Children.Add(_status);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        _results = new ListBox
        {
            ItemsSource = _resultItems,
            ItemTemplate = new FuncDataTemplate<OriginalDramaLookupRow>((row, _) =>
                row is null ? new Panel() : BuildResultRow(row)),
        };
        Grid.SetRow(_results, 3);
        root.Children.Add(_results);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var copy = new Button { Content = "复制", MinWidth = 88 };
        copy.Click += async (_, _) => await CopyAsync();
        var export = new Button { Content = "导出 Excel", MinWidth = 108 };
        export.Click += async (_, _) => await ExportAsync(openAfterSave: false);
        var saveAndOpen = new Button { Content = "保存并打开 Excel", MinWidth = 140 };
        saveAndOpen.Click += async (_, _) => await ExportAsync(openAfterSave: true);
        var close = new Button { Content = "关闭", MinWidth = 88 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(export);
        buttons.Children.Add(saveAndOpen);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;
        Closed += (_, _) => _cancellation.Cancel();
    }

    public static Task ShowAsync(Window owner, string excelPath, ClientSettings settings) =>
        new OriginalDramaLookupDialog(excelPath, settings).ShowDialog(owner);

    private async Task QueryAsync()
    {
        if (_running)
            return;
        var titles = (_input.Text ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (titles.Length == 0)
        {
            _status.Text = "请输入新剧名。";
            return;
        }

        _running = true;
        _query.IsEnabled = false;
        _rows = [];
        _resultItems.Clear();
        try
        {
            var progress = new Progress<string>(text => _status.Text = text);
            _rows = await OriginalDramaLookupService.LookupAsync(
                titles,
                _excelPath,
                _settings,
                progress,
                _cancellation.Token);
            foreach (var row in _rows)
                _resultItems.Add(row);
            var found = _rows.Count(row => row.Status == "已找到");
            _status.Text = $"完成 {found}/{_rows.Count}";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "查询已取消。";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _running = false;
            if (!_cancellation.IsCancellationRequested)
                _query.IsEnabled = true;
        }
    }

    private async Task CopyAsync()
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, _rows.Select(row => row.Format()));
        if (text.Length == 0)
        {
            _status.Text = "没有可复制的结果。";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(text);
        _status.Text = "已复制。";
    }

    private async Task ExportAsync(bool openAfterSave)
    {
        if (_rows.Count == 0)
        {
            _status.Text = "请先查询。";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = openAfterSave ? "保存并打开原剧查询" : "导出原剧查询",
            SuggestedFileName = $"原剧查询-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
            FileTypeChoices = [new FilePickerFileType("Excel 工作簿") { Patterns = ["*.xlsx"] }],
        });
        if (file is null)
            return;

        var path = file.TryGetLocalPath();
        await using (var stream = await file.OpenWriteAsync())
        {
            await Task.Run(() => OriginalDramaLookupService.Export(stream, _rows));
        }

        if (!openAfterSave)
        {
            _status.Text = "已导出 Excel。";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _status.Text = "已保存 Excel，但无法打开该位置的文件。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            _status.Text = "已保存并打开 Excel。";
        }
        catch (Exception ex)
        {
            _status.Text = $"已保存 Excel，打开失败：{ex.Message}";
        }
    }

    private static Control BuildResultRow(OriginalDramaLookupRow row)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(2, 8, 2, 8) };
        panel.Children.Add(new TextBlock
        {
            Text = Headline(row),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        var facts = FactLine(row);
        if (facts.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = facts,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var intro = row.Field("简介");
        if (intro.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = intro,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 44,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.DimGray,
            });
        }

        if (row.Status != "已找到" && row.Candidates.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "相近结果：" + string.Join("、", row.Candidates),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.IndianRed,
            });
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#E6E6E6")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = panel,
        };
    }

    private static string Headline(OriginalDramaLookupRow row)
    {
        var parts = new List<string> { row.NewTitle };
        if (row.OriginalTitle.Length > 0 &&
            !string.Equals(row.OriginalTitle, row.NewTitle, StringComparison.Ordinal))
            parts.Add(row.OriginalTitle);
        if (row.Orientation.Length > 0)
            parts.Add(row.Orientation);
        if (row.Genre.Length > 0)
            parts.Add(row.Genre);
        parts.Add(row.Status);
        return string.Join("    ", parts);
    }

    private static string FactLine(OriginalDramaLookupRow row)
    {
        var parts = OriginalDramaLookupRow.DetailNames
            .Where(name => name != "简介")
            .Select(name =>
            {
                var value = row.Field(name);
                return string.IsNullOrWhiteSpace(value) ? "" : $"{name} {value}";
            })
            .Where(item => item.Length > 0);
        return string.Join("    ", parts);
    }
}
