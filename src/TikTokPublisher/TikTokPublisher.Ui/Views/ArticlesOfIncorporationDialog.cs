using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class ArticlesOfIncorporationDialog : Window
{
    private readonly TextBox _filingNumber = Box();
    private readonly TextBox _businessId = Box();
    private readonly TextBox _filedAt = Box();
    private readonly TextBox _agentName = Box();
    private readonly TextBox _agentStreet = Box();
    private readonly TextBox _agentCity = Box();
    private readonly TextBox _signedOn = Box();
    private readonly TextBox _incorporatorName = Box();
    private readonly CheckBox _alsoChinese = new()
    {
        Content = "同步生成中文PDF",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _status = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private readonly string? _suggestedDirectory;

    private ArticlesOfIncorporationDialog(string? suggestedDirectory)
    {
        _suggestedDirectory = suggestedDirectory;
        Title = "生成公司注册 PDF";
        Width = 920;
        MinWidth = 760;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _incorporatorName.IsReadOnly = true;
        _incorporatorName.Watermark = "由注册人名称自动生成";
        _agentName.TextChanged += (_, _) => SyncShortName();
        RandomizeFilingStamp();

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            RowSpacing = 14,
        };
        root.Children.Add(new TextBlock
        {
            Text = "填写之前从公司注册文件中消除的内容，按原位置生成一份新 PDF。公司名称、费用和其他固定文字保持模板原样。留空的项目不会写入。注册人简称随注册人名称自动生成，注册人地址同时写到文件中的两处地址。",
            TextWrapping = TextWrapping.Wrap,
        });

        var form = BuildForm();
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var randomize = new Button { Content = "随机生成", MinWidth = 108 };
        randomize.Click += (_, _) => RandomizeFilingStamp();
        var generate = new Button { Content = "生成 PDF", MinWidth = 108, IsDefault = true };
        generate.Click += async (_, _) => await GenerateAsync();
        buttons.Children.Add(randomize);
        var cancel = new Button { Content = "关闭", MinWidth = 88, IsCancel = true };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(generate);
        buttons.Children.Add(cancel);
        var actions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        actions.Children.Add(_alsoChinese);
        Grid.SetColumn(buttons, 1);
        actions.Children.Add(buttons);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        Content = root;
    }

    public static Task ShowAsync(Window owner, string? suggestedDirectory = null)
    {
        var dialog = new ArticlesOfIncorporationDialog(suggestedDirectory);
        return dialog.ShowDialog(owner);
    }

    private Grid BuildForm()
    {
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,18,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            RowSpacing = 8,
        };
        Place(form, "备案号", _filingNumber, 0, 0);
        Place(form, "Business ID", _businessId, 0, 1);
        Place(form, "备案时间", _filedAt, 1, 0);
        Place(form, "签署日期", _signedOn, 1, 1);
        Place(form, "注册人名称", _agentName, 2, 0);
        Place(form, "注册人简称", _incorporatorName, 2, 1);
        Place(form, "注册人地址", _agentStreet, 3, 0);
        Place(form, "城市", _agentCity, 3, 1);
        return form;
    }

    private static void Place(Grid form, string label, TextBox box, int row, int pair)
    {
        var column = pair * 3;
        var caption = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, column);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, column + 1);
        form.Children.Add(caption);
        form.Children.Add(box);
    }

    private static TextBox Box() => new() { MinWidth = 240, Watermark = "留空则不写入" };

    private ArticlesOfIncorporationFields ReadFields() => new(
        _filingNumber.Text ?? string.Empty,
        _businessId.Text ?? string.Empty,
        _filedAt.Text ?? string.Empty,
        _agentName.Text ?? string.Empty,
        _agentStreet.Text ?? string.Empty,
        _agentCity.Text ?? string.Empty,
        _signedOn.Text ?? string.Empty,
        ArticlesOfIncorporationFields.ToShortName(_agentName.Text),
        _agentStreet.Text ?? string.Empty,
        _agentCity.Text ?? string.Empty);

    private void SyncShortName() =>
        _incorporatorName.Text = ArticlesOfIncorporationFields.ToShortName(_agentName.Text);

    private void RandomizeFilingStamp()
    {
        var stamp = ArticlesOfIncorporationFields.CreateRandomFilingStamp(DateOnly.FromDateTime(DateTime.Today));
        _filingNumber.Text = stamp.FilingNumber;
        _businessId.Text = stamp.BusinessId;
        _filedAt.Text = stamp.FiledAt;
        _signedOn.Text = stamp.SignedOn;
        _agentName.Text = stamp.RegistrantName;
        _agentStreet.Text = stamp.Street;
        _agentCity.Text = stamp.City;
        SyncShortName();
        _status.Text = "已随机生成备案信息、注册人名称和地址。";
    }

    private async Task GenerateAsync()
    {
        var fields = ReadFields();
        if (!fields.HasAnyValue())
        {
            _status.Text = "请至少填写一项。";
            return;
        }

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(_suggestedDirectory) && Directory.Exists(_suggestedDirectory))
            start = await StorageProvider.TryGetFolderFromPathAsync(_suggestedDirectory);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存公司注册 PDF",
            SuggestedFileName = "Tangible Group INC.pdf",
            SuggestedStartLocation = start,
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (file is null)
        {
            _status.Text = "已取消保存。";
            return;
        }

        try
        {
            var path = file.TryGetLocalPath();
            string? chinesePath = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var writeChinese = _alsoChinese.IsChecked == true;
                chinesePath = writeChinese ? ArticlesOfIncorporationPdfService.ChineseOutputPath(path) : null;
                await Task.Run(() =>
                {
                    ArticlesOfIncorporationPdfService.Render(path, fields);
                    if (chinesePath is not null)
                        ArticlesOfIncorporationPdfService.RenderChinese(chinesePath, fields);
                });
            }
            else
            {
                var temp = Path.Combine(Path.GetTempPath(), $"articles-{Guid.NewGuid():N}.pdf");
                try
                {
                    await Task.Run(() => ArticlesOfIncorporationPdfService.Render(temp, fields));
                    await using var input = File.OpenRead(temp);
                    await using var output = await file.OpenWriteAsync();
                    await input.CopyToAsync(output);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }

            _status.Text = string.IsNullOrWhiteSpace(path)
                ? "已生成 PDF。"
                : chinesePath is null
                    ? $"已生成：{path}"
                    : $"已生成：{path}{Environment.NewLine}中文：{chinesePath}";
        }
        catch (Exception ex)
        {
            _status.Text = $"生成失败：{ex.Message}";
        }
    }
}
