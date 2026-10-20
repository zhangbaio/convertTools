using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class ArticlesOfIncorporationDialog : Window
{
    private readonly TextBox _companyName = Box();
    private readonly TextBox _companyNameChinese = Box();
    private readonly TextBox _email = Box();
    private readonly TextBox _filingNumber = Box();
    private readonly TextBox _businessId = Box();
    private readonly TextBox _filedAt = Box();
    private readonly TextBox _agentName = Box();
    private readonly TextBox _agentStreet = Box();
    private readonly TextBox _agentCity = Box();
    private readonly TextBox _signedOn = Box();
    private readonly TextBox _incorporatorName = Box();
    private readonly TextBox _kyFilingNumber = Box();
    private readonly TextBox _kyFiledAt = Box();
    private readonly TextBox _kyCompanyName = Box();
    private readonly TextBox _kyCompanyNameChinese = Box();
    private readonly TextBox _kyStreet = Box();
    private readonly TextBox _kyCity = Box();
    private readonly TextBox _kyEffectiveOn = Box();
    private readonly TabControl _tabs = new();
    private readonly CheckBox _alsoChinese = new()
    {
        Content = "同步生成中文PDF",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _status = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private readonly string? _suggestedDirectory;
    private bool _syncingShortName;
    private bool _syncingCompanyChinese;
    private bool _companyChineseEdited;
    private bool _syncingKentuckyChinese;
    private bool _kentuckyChineseEdited;

    private ArticlesOfIncorporationDialog(string? suggestedDirectory)
    {
        _suggestedDirectory = suggestedDirectory;
        Title = "生成公司注册 PDF";
        Width = 920;
        MinWidth = 760;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var savedName = RegistrantNameStore.Load();
        _agentName.Text = savedName.Name;
        _incorporatorName.Text = savedName.ShortName;
        _agentName.TextChanged += (_, _) =>
        {
            SyncShortName();
            RegistrantNameStore.Save(_agentName.Text, _incorporatorName.Text);
        };
        _incorporatorName.TextChanged += (_, _) =>
        {
            if (_syncingShortName)
                return;
            RegistrantNameStore.Save(_agentName.Text, _incorporatorName.Text);
        };
        _companyName.TextChanged += (_, _) =>
        {
            if (!_companyChineseEdited)
                ApplyCompanyChinese(ArticlesChineseText.TranslateCompany(_companyName.Text));
        };
        _companyNameChinese.TextChanged += (_, _) =>
        {
            if (!_syncingCompanyChinese)
                _companyChineseEdited = true;
        };
        _kyCompanyName.TextChanged += (_, _) =>
        {
            if (!_kentuckyChineseEdited)
                ApplyKentuckyChinese(ArticlesChineseText.TranslateKentuckyCompany(_kyCompanyName.Text));
        };
        _kyCompanyNameChinese.TextChanged += (_, _) =>
        {
            if (!_syncingKentuckyChinese)
                _kentuckyChineseEdited = true;
        };
        RandomizeKentucky();
        RandomizeFilingStamp();

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 14,
        };

        _tabs.Items.Add(new TabItem { Header = "模板1", Content = BuildForm() });
        _tabs.Items.Add(new TabItem { Header = "模板2", Content = BuildKentuckyForm() });
        root.Children.Add(_tabs);

        Grid.SetRow(_status, 1);
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var randomize = new Button { Content = "随机生成", MinWidth = 108 };
        randomize.Click += (_, _) =>
        {
            if (_tabs.SelectedIndex == 1)
                RandomizeKentucky();
            else
                RandomizeFilingStamp();
        };
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
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Content = root;
    }

    public static Task ShowAsync(Window owner, string? suggestedDirectory = null)
    {
        var dialog = new ArticlesOfIncorporationDialog(suggestedDirectory);
        return dialog.ShowDialog(owner);
    }

    public static ArticlesOfIncorporationDialog CreateStandaloneWindow() => new(null);

    private Grid BuildForm()
    {
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,18,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 8,
        };
        Place(form, "企业名称", _companyName, 0, 0);
        Place(form, "企业邮箱", _email, 0, 1);
        PlaceWide(form, "企业名称：中文", _companyNameChinese, 1);
        Place(form, "备案号", _filingNumber, 2, 0);
        Place(form, "Business ID", _businessId, 2, 1);
        Place(form, "备案时间", _filedAt, 3, 0);
        Place(form, "签署日期", _signedOn, 3, 1);
        Place(form, "注册人名称", _agentName, 4, 0);
        Place(form, "注册人简称", _incorporatorName, 4, 1);
        Place(form, "注册人地址", _agentStreet, 5, 0);
        Place(form, "城市", _agentCity, 5, 1);
        return form;
    }

    private Grid BuildKentuckyForm()
    {
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,18,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            RowSpacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Place(form, "备案号", _kyFilingNumber, 0, 0);
        Place(form, "备案时间", _kyFiledAt, 0, 1);
        Place(form, "公司名称", _kyCompanyName, 1, 0);
        Place(form, "生效日期", _kyEffectiveOn, 1, 1);
        PlaceWide(form, "公司名称：中文", _kyCompanyNameChinese, 2);
        Place(form, "街道", _kyStreet, 3, 0);
        Place(form, "城市", _kyCity, 3, 1);
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

    private static void PlaceWide(Grid form, string label, TextBox box, int row)
    {
        var caption = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        Grid.SetColumnSpan(box, 4);
        form.Children.Add(caption);
        form.Children.Add(box);
    }

    private static TextBox Box() => new() { MinWidth = 240, Watermark = "留空则不写入" };

    private static string WithLibraryNotice(string message)
    {
        var notice = CompanyNameLibraries.StartupNotice;
        return string.IsNullOrWhiteSpace(notice) ? message : message + Environment.NewLine + notice;
    }

    private static string SuggestedPdfName(string? companyName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((companyName ?? string.Empty).Trim().Select(ch => invalid.Contains(ch) ? ' ' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(name) ? "公司注册.pdf" : name + ".pdf";
    }

    private ArticlesOfIncorporationFields ReadFields() => new(
        _filingNumber.Text ?? string.Empty,
        _businessId.Text ?? string.Empty,
        _filedAt.Text ?? string.Empty,
        _agentName.Text ?? string.Empty,
        _agentStreet.Text ?? string.Empty,
        _agentCity.Text ?? string.Empty,
        _signedOn.Text ?? string.Empty,
        _incorporatorName.Text ?? string.Empty,
        _agentStreet.Text ?? string.Empty,
        _agentCity.Text ?? string.Empty,
        _companyName.Text ?? string.Empty,
        _email.Text ?? string.Empty,
        _companyNameChinese.Text ?? string.Empty);

    private void SyncShortName()
    {
        _syncingShortName = true;
        _incorporatorName.Text = ArticlesOfIncorporationFields.ToShortName(_agentName.Text);
        _syncingShortName = false;
    }

    private void ApplyCompanyChinese(string value)
    {
        _syncingCompanyChinese = true;
        _companyNameChinese.Text = value;
        _syncingCompanyChinese = false;
    }

    private void ApplyKentuckyChinese(string value)
    {
        _syncingKentuckyChinese = true;
        _kyCompanyNameChinese.Text = value;
        _syncingKentuckyChinese = false;
    }

    private void RandomizeFilingStamp()
    {
        var stamp = ArticlesOfIncorporationFields.CreateRandomFilingStamp(DateOnly.FromDateTime(DateTime.Today));
        _filingNumber.Text = stamp.FilingNumber;
        _businessId.Text = stamp.BusinessId;
        _filedAt.Text = stamp.FiledAt;
        _signedOn.Text = stamp.SignedOn;
        _agentStreet.Text = stamp.Street;
        _agentCity.Text = stamp.City;
        _email.Text = stamp.Email;
        var company = CompanyNameLibraries.TakeTemplate1();
        if (company.Exhausted)
        {
            KeepCompanyName(company, _companyName, ApplyCompanyChinese, ref _companyChineseEdited);
            _status.Text = WithLibraryNotice(CompanyNameLibraries.Template1ExhaustedMessage);
            return;
        }

        _companyChineseEdited = false;
        _companyName.Text = company.English;
        ApplyCompanyChinese(company.Chinese);
        _status.Text = WithLibraryNotice("已随机生成备案信息、企业名称、中文名称、邮箱和地址。");
    }

    private void RandomizeKentucky()
    {
        var previous = KentuckyRandomHistory.Load();
        var current = ReadKentuckyFields();
        if (current.HasAnyValue())
            previous.Add(current);
        var company = CompanyNameLibraries.TakeTemplate2();
        var companyName = company.Exhausted && !string.IsNullOrWhiteSpace(_kyCompanyName.Text)
            ? _kyCompanyName.Text
            : company.English;
        var fields = KentuckyArticlesPdfService.CreateRandom(
            DateOnly.FromDateTime(DateTime.Today),
            previous: previous,
            companyName: string.IsNullOrWhiteSpace(companyName) ? null : companyName);
        _kyFilingNumber.Text = fields.FilingNumber;
        _kyFiledAt.Text = fields.FiledAt;
        _kyStreet.Text = fields.Street;
        _kyCity.Text = fields.CityLine;
        _kyEffectiveOn.Text = fields.EffectiveOn;
        KentuckyRandomHistory.Remember(fields);
        if (company.Exhausted)
        {
            KeepCompanyName(company, _kyCompanyName, ApplyKentuckyChinese, ref _kentuckyChineseEdited);
            _status.Text = WithLibraryNotice(CompanyNameLibraries.Template2ExhaustedMessage);
            return;
        }

        _kentuckyChineseEdited = false;
        _kyCompanyName.Text = company.English;
        ApplyKentuckyChinese(company.Chinese);
        _status.Text = WithLibraryNotice("已随机生成肯塔基备案号、时间、公司名称、中文名称、地址和生效日期。");
    }

    private static void KeepCompanyName(
        CompanyNameChoice company,
        TextBox box,
        Action<string> applyChinese,
        ref bool edited)
    {
        if (!string.IsNullOrWhiteSpace(box.Text) || company.English.Length == 0)
            return;
        edited = false;
        box.Text = company.English;
        applyChinese(company.Chinese);
    }

    private KentuckyArticlesFields ReadKentuckyFields() => new(
        _kyFilingNumber.Text ?? string.Empty,
        _kyFiledAt.Text ?? string.Empty,
        _kyCompanyName.Text ?? string.Empty,
        _kyStreet.Text ?? string.Empty,
        _kyCity.Text ?? string.Empty,
        _kyEffectiveOn.Text ?? string.Empty,
        _kyCompanyNameChinese.Text ?? string.Empty);

    private async Task GenerateAsync()
    {
        var kentucky = _tabs.SelectedIndex == 1;
        var fields = kentucky ? null : ReadFields();
        var kentuckyFields = kentucky ? ReadKentuckyFields() : null;
        if (kentucky ? !kentuckyFields!.HasAnyValue() : !fields!.HasAnyValue())
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
            SuggestedFileName = SuggestedPdfName(kentucky ? _kyCompanyName.Text : _companyName.Text),
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
                    if (kentucky)
                    {
                        KentuckyArticlesPdfService.Render(path, kentuckyFields!);
                        if (chinesePath is not null)
                            KentuckyArticlesPdfService.RenderChinese(chinesePath, kentuckyFields);
                    }
                    else
                    {
                        ArticlesOfIncorporationPdfService.Render(path, fields!);
                        if (chinesePath is not null)
                            ArticlesOfIncorporationPdfService.RenderChinese(chinesePath, fields);
                    }
                });
            }
            else
            {
                var temp = Path.Combine(Path.GetTempPath(), $"articles-{Guid.NewGuid():N}.pdf");
                try
                {
                    await Task.Run(() =>
                    {
                        if (kentucky)
                            KentuckyArticlesPdfService.Render(temp, kentuckyFields!);
                        else
                            ArticlesOfIncorporationPdfService.Render(temp, fields!);
                    });
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

file static class RegistrantNameStore
{
    private const string DefaultName = "ANTHONY CLYDE CLUNIS";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".registration_pdf",
        "registrant.json");

    public static (string Name, string ShortName) Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return Default();
            var saved = JsonSerializer.Deserialize<SavedRegistrant>(File.ReadAllText(FilePath));
            var name = string.IsNullOrWhiteSpace(saved?.Name) ? DefaultName : saved.Name.Trim();
            var shortName = string.IsNullOrWhiteSpace(saved?.ShortName)
                ? ArticlesOfIncorporationFields.ToShortName(name)
                : saved.ShortName.Trim();
            return (name, shortName);
        }
        catch
        {
            return Default();
        }
    }

    public static void Save(string? name, string? shortName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var payload = new SavedRegistrant(
                (name ?? string.Empty).Trim(),
                (shortName ?? string.Empty).Trim());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload));
        }
        catch
        {
            // A failed preference write should not block PDF generation.
        }
    }

    private static (string Name, string ShortName) Default() =>
        (DefaultName, ArticlesOfIncorporationFields.ToShortName(DefaultName));

    private sealed record SavedRegistrant(string Name, string ShortName);
}

file static class KentuckyRandomHistory
{
    private const int MaxEntries = 300;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".registration_pdf",
        "kentucky-random-history.json");

    public static List<KentuckyArticlesFields> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            return JsonSerializer.Deserialize<List<KentuckyArticlesFields>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Remember(KentuckyArticlesFields fields)
    {
        try
        {
            var history = Load();
            history.Add(fields);
            if (history.Count > MaxEntries)
                history.RemoveRange(0, history.Count - MaxEntries);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(history));
        }
        catch
        {
            // Remembering a draw should not block the next random generation.
        }
    }
}
