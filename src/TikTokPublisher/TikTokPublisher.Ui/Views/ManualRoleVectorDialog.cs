using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed record ManualRoleVectorDialogResult(
    ManualRoleVectorMode Mode,
    IReadOnlyList<ManualRoleCharacter> Characters,
    string? FinalImagePath,
    bool Locked);

public sealed class ManualRoleVectorDialog : Window
{
    private readonly ComboBox _mode;
    private readonly NumericUpDown _count;
    private readonly CheckBox _locked;
    private readonly StackPanel _referencePanel;
    private readonly StackPanel _finalPanel;
    private readonly TextBlock _finalPath;
    private readonly Image _finalPreview;
    private readonly TextBlock _message;
    private readonly List<ReferenceRow> _rows = [];
    private string? _selectedFinalPath;

    private ManualRoleVectorDialog(string projectName, ManualRoleVectorConfiguration existing)
    {
        Title = $"角色素材配置 - {projectName}";
        Width = 900;
        Height = 720;
        MinWidth = 760;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            RowSpacing = 12,
        };
        root.Children.Add(new TextBlock
        {
            Text = "只需为每个角色选择一张清晰人物参考图。系统会逐人生成全身定妆图，再自动合成角色矢量图；强制重跑不会重新选人。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        var settings = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,250,Auto,110,Auto"),
            ColumnSpacing = 10,
        };
        settings.Children.Add(new TextBlock { Text = "使用方式", VerticalAlignment = VerticalAlignment.Center });
        _mode = new ComboBox
        {
            ItemsSource = new[]
            {
                new ModeOption("自动选择参考图", ManualRoleVectorMode.Auto),
                new ModeOption("手动指定人物参考图", ManualRoleVectorMode.ReferencesOnly),
                new ModeOption("直接使用成品角色矢量图", ManualRoleVectorMode.FinalImage),
            },
            SelectedIndex = existing.Mode switch
            {
                ManualRoleVectorMode.ReferencesOnly or ManualRoleVectorMode.Paired => 1,
                ManualRoleVectorMode.FinalImage => 2,
                _ => 0,
            },
        };
        Grid.SetColumn(_mode, 1);
        settings.Children.Add(_mode);
        var countLabel = new TextBlock { Text = "人数", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(countLabel, 2);
        settings.Children.Add(countLabel);
        _count = new NumericUpDown
        {
            Minimum = 3,
            Maximum = 6,
            Increment = 1,
            Value = Math.Clamp(existing.Characters.Count == 0 ? 3 : existing.Characters.Count, 3, 6),
        };
        Grid.SetColumn(_count, 3);
        settings.Children.Add(_count);
        _locked = new CheckBox
        {
            Content = "锁定人工人物",
            IsChecked = existing.Mode == ManualRoleVectorMode.Auto || existing.Locked,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_locked, 4);
        settings.Children.Add(_locked);
        Grid.SetRow(settings, 1);
        root.Children.Add(settings);

        var content = new Grid();
        _referencePanel = new StackPanel { Spacing = 8 };
        _referencePanel.Children.Add(BuildReferenceHeader());
        for (var index = 0; index < 6; index++)
        {
            var existingRole = existing.Characters.ElementAtOrDefault(index);
            var row = new ReferenceRow(index + 1, existingRole);
            row.PickButton.Click += async (_, _) => await PickReferenceAsync(row);
            _rows.Add(row);
            _referencePanel.Children.Add(row.Container);
        }
        content.Children.Add(new ScrollViewer
        {
            Content = _referencePanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        });

        _selectedFinalPath = existing.FinalImagePath;
        _finalPath = new TextBlock
        {
            Text = FormatPath(_selectedFinalPath, "尚未选择成品图片"),
            TextWrapping = TextWrapping.Wrap,
        };
        _finalPreview = CreatePreview(_selectedFinalPath, 260, 142);
        var pickFinal = new Button { Content = "选择 2342×1280 成品图片", MinWidth = 220 };
        pickFinal.Click += async (_, _) => await PickFinalImageAsync();
        _finalPanel = new StackPanel { Spacing = 12 };
        _finalPanel.Children.Add(new TextBlock
        {
            Text = "成品必须是 2342×1280 的有效图片。保存后将转换为标准 PNG，并直接作为角色矢量图使用。",
            TextWrapping = TextWrapping.Wrap,
        });
        _finalPanel.Children.Add(pickFinal);
        _finalPanel.Children.Add(_finalPreview);
        _finalPanel.Children.Add(_finalPath);
        content.Children.Add(_finalPanel);
        Grid.SetRow(content, 2);
        root.Children.Add(content);

        _message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_message, 3);
        root.Children.Add(_message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var save = new Button { Content = "保存参考图", MinWidth = 108 };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        _mode.SelectionChanged += (_, _) => RefreshModeVisibility();
        _count.ValueChanged += (_, _) => RefreshRows();
        Content = root;
        Opened += (_, _) =>
        {
            RefreshModeVisibility();
            RefreshRows();
        };
    }

    public static Task<ManualRoleVectorDialogResult?> ShowAsync(
        Window owner,
        string projectName,
        ManualRoleVectorConfiguration existing) =>
        new ManualRoleVectorDialog(projectName, existing)
            .ShowDialog<ManualRoleVectorDialogResult?>(owner);

    private static Control BuildReferenceHeader()
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,180,120,*,100"),
            ColumnSpacing = 10,
        };
        foreach (var (text, column) in new[]
                 {
                     ("顺序", 0), ("角色名称", 1), ("参考预览", 2), ("人物参考图", 3), ("", 4),
                 })
        {
            var label = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(label, column);
            header.Children.Add(label);
        }
        return header;
    }

    private async Task PickReferenceAsync(ReferenceRow row)
    {
        if (StorageProvider is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"选择角色 {row.Order} 的清晰人物参考图",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        row.SetReferencePath(path);
        SetMessage(string.Empty, false);
    }

    private async Task PickFinalImageAsync()
    {
        if (StorageProvider is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择成品角色矢量图（2342×1280）",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _selectedFinalPath = path;
        _finalPath.Text = path;
        SetPreview(_finalPreview, path);
    }

    private void Save()
    {
        var mode = SelectedMode;
        if (mode == ManualRoleVectorMode.Auto)
        {
            Close(new ManualRoleVectorDialogResult(mode, [], null, false));
            return;
        }
        if (mode == ManualRoleVectorMode.FinalImage)
        {
            if (string.IsNullOrWhiteSpace(_selectedFinalPath) || !File.Exists(_selectedFinalPath))
            {
                SetMessage("请先选择有效的成品角色矢量图。", true);
                return;
            }
            Close(new ManualRoleVectorDialogResult(mode, [], _selectedFinalPath, _locked.IsChecked == true));
            return;
        }

        var count = Math.Clamp((int)(_count.Value ?? 3), 3, 6);
        var characters = _rows.Take(count).Select(row => new ManualRoleCharacter(
            row.Order,
            (row.Name.Text ?? string.Empty).Trim(),
            string.Empty,
            row.ReferencePath ?? string.Empty)).ToArray();
        var invalid = characters.FirstOrDefault(character =>
            string.IsNullOrWhiteSpace(character.Name) || !File.Exists(character.ReferencePath));
        if (invalid is not null)
        {
            SetMessage($"第 {invalid.Order} 行缺少角色名称或人物参考图。", true);
            return;
        }
        Close(new ManualRoleVectorDialogResult(
            ManualRoleVectorMode.ReferencesOnly,
            characters,
            null,
            _locked.IsChecked == true));
    }

    private ManualRoleVectorMode SelectedMode =>
        (_mode.SelectedItem as ModeOption)?.Mode ?? ManualRoleVectorMode.Auto;

    private void RefreshModeVisibility()
    {
        _referencePanel.IsVisible = SelectedMode == ManualRoleVectorMode.ReferencesOnly;
        _finalPanel.IsVisible = SelectedMode == ManualRoleVectorMode.FinalImage;
        _count.IsEnabled = SelectedMode == ManualRoleVectorMode.ReferencesOnly;
        _locked.IsEnabled = SelectedMode != ManualRoleVectorMode.Auto;
    }

    private void RefreshRows()
    {
        var count = Math.Clamp((int)(_count.Value ?? 3), 3, 6);
        foreach (var row in _rows) row.Container.IsVisible = row.Order <= count;
    }

    private void SetMessage(string message, bool error)
    {
        _message.Text = message;
        _message.Foreground = error ? Brushes.IndianRed : Brushes.SeaGreen;
    }

    private static Image CreatePreview(string? path, double width, double height)
    {
        var image = new Image
        {
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        SetPreview(image, path);
        return image;
    }

    private static void SetPreview(Image image, string? path)
    {
        try { image.Source = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new Bitmap(path) : null; }
        catch { image.Source = null; }
    }

    private static string FormatPath(string? path, string empty) =>
        string.IsNullOrWhiteSpace(path) ? empty : path;

    private sealed record ModeOption(string Label, ManualRoleVectorMode Mode)
    {
        public override string ToString() => Label;
    }

    private sealed class ReferenceRow
    {
        public int Order { get; }
        public Grid Container { get; }
        public TextBox Name { get; }
        public Button PickButton { get; }
        public string? ReferencePath { get; private set; }
        private readonly Image _preview;
        private readonly TextBlock _path;

        public ReferenceRow(int order, ManualRoleCharacter? existing)
        {
            Order = order;
            ReferencePath = existing?.ReferencePath;
            Container = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("50,180,120,*,100"),
                ColumnSpacing = 10,
                MinHeight = 106,
            };
            Container.Children.Add(new TextBlock
            {
                Text = order.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            Name = new TextBox
            {
                Text = existing?.Name ?? $"角色{order}",
                Watermark = $"角色{order}",
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(Name, 1);
            Container.Children.Add(Name);
            _preview = CreatePreview(ReferencePath, 96, 100);
            Grid.SetColumn(_preview, 2);
            Container.Children.Add(_preview);
            _path = new TextBlock
            {
                Text = FormatPath(ReferencePath, "未选择人物参考图"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_path, 3);
            Container.Children.Add(_path);
            PickButton = new Button { Content = "选择参考图", MinWidth = 94, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(PickButton, 4);
            Container.Children.Add(PickButton);
        }

        public void SetReferencePath(string path)
        {
            ReferencePath = path;
            _path.Text = path;
            SetPreview(_preview, path);
        }
    }
}
