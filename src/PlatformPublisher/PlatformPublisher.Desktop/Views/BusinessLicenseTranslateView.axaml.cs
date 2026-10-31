using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.Licensing;
using ShortDrama.Infrastructure.Process;
using ShortDrama.Infrastructure.Rendering;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Views;

public partial class BusinessLicenseTranslateView : UserControl
{
    private readonly List<BusinessLicenseDraft> _drafts = [];
    private BusinessLicenseDraft? _current;
    private bool _loading;

    public BusinessLicenseTranslateView()
    {
        InitializeComponent();
    }

    private async void PickImages_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择营业执照图片",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] },
            ],
        });
        if (files.Count == 0) return;
        SaveCurrent();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (_drafts.Any(item => string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            _drafts.Add(new BusinessLicenseDraft { SourcePath = path });
        }
        BindFiles();
        StatusText.Text = $"已选择 {_drafts.Count} 张图片。";
    }

    private async void Recognize_Click(object? sender, RoutedEventArgs e)
    {
        if (_drafts.Count == 0)
        {
            StatusText.Text = "请先选择营业执照图片。";
            return;
        }
        StatusText.Text = "正在识别…";
        var failures = new List<string>();
        foreach (var draft in _drafts)
        {
            try
            {
                var text = await BusinessLicenseOcr.RecognizeAsync(draft.SourcePath, CancellationToken.None);
                var parsed = BusinessLicenseParser.Parse(draft.SourcePath, text);
                CopyParsed(draft, parsed);
                ClearMachineTranslation(draft);
                BusinessLicenseTranslator.ApplyFixed(draft);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(draft.SourcePath)}：{ex.Message}");
            }
        }
        Show(_current ?? _drafts[0]);
        StatusText.Text = failures.Count == 0
            ? "识别完成。请核对中文，再点「按中文重新翻译」。"
            : "部分图片识别失败：" + string.Join(" ", failures);
    }

    private async void Retranslate_Click(object? sender, RoutedEventArgs e)
    {
        SaveCurrent();
        if (_drafts.Count == 0)
        {
            StatusText.Text = "请先选择并识别营业执照。";
            return;
        }
        BusinessLicenseTranslationOptions options;
        try
        {
            options = CreateOptions();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        var failures = new List<string>();
        for (var i = 0; i < _drafts.Count; i++)
        {
            var draft = _drafts[i];
            StatusText.Text = $"正在翻译 {i + 1}/{_drafts.Count}…";
            BusinessLicenseTranslator.ApplyFixed(draft);
            try
            {
                await BusinessLicenseAiTranslator.TranslateAsync(draft, options);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(draft.SourcePath)}：{ex.Message}");
            }
        }
        if (_current is not null) Show(_current);
        StatusText.Text = failures.Count == 0
            ? "已按当前中文翻译。请核对英文后再生成。"
            : "翻译失败：" + string.Join(" ", failures);
    }

    private async void Generate_Click(object? sender, RoutedEventArgs e)
    {
        SaveCurrent();
        if (_drafts.Count == 0 || _drafts.All(item => string.IsNullOrWhiteSpace(item.EnglishName)))
        {
            StatusText.Text = "请先识别，并确认英文名称。";
            return;
        }
        var options = CreateOptions();
        foreach (var draft in _drafts.Where(item => !string.IsNullOrWhiteSpace(item.EnglishName)))
            BusinessLicenseAiTranslator.Remember(draft, options.GlossaryPath);
        var logger = LoggerFactory.Create(builder => { }).CreateLogger<LibreOfficeDocumentRenderService>();
        var renderer = new LibreOfficeDocumentRenderService(new ExternalProcessRunner(), logger);
        var written = new List<string>();
        var failures = new List<string>();
        foreach (var draft in _drafts.Where(item => !string.IsNullOrWhiteSpace(item.EnglishName)))
        {
            var docx = BusinessLicenseDocumentWriter.OutputPath(draft, ".docx");
            try
            {
                BusinessLicenseDocumentWriter.Write(docx, draft);
                written.Add(docx);
                var folder = Path.GetDirectoryName(docx)!;
                await renderer.ConvertDocxToPdfAsync(docx, folder, CancellationToken.None);
                written.Add(Path.ChangeExtension(docx, ".pdf"));
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(draft.SourcePath)}：{ex.Message}");
            }
        }
        StatusText.Text = failures.Count == 0
            ? "已生成：" + string.Join("；", written)
            : "已生成：" + string.Join("；", written) + "。失败：" + string.Join(" ", failures);
    }

    private void FileBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SaveCurrent();
        if (FileBox.SelectedIndex >= 0 && FileBox.SelectedIndex < _drafts.Count)
            Show(_drafts[FileBox.SelectedIndex]);
    }

    private void BindFiles()
    {
        _loading = true;
        FileBox.ItemsSource = _drafts.Select(item => item.DisplayName).ToList();
        if (_drafts.Count > 0)
        {
            FileBox.SelectedIndex = 0;
            Show(_drafts[0]);
        }
        _loading = false;
    }

    private void Show(BusinessLicenseDraft draft)
    {
        _current = draft;
        _loading = true;
        CreditBox.Text = draft.CreditCode;
        EnglishCreditBox.Text = draft.CreditCode;
        NameBox.Text = draft.Name;
        TypeBox.Text = draft.Type;
        PersonLabel.Text = draft.PersonRole;
        PersonBox.Text = draft.PersonName;
        ExtraLabel.Text = draft.ExtraLabel;
        ExtraBox.Text = draft.ExtraValue;
        DateLabel.Text = draft.DateLabel;
        DateBox.Text = draft.DateValue;
        AddressLabel.Text = draft.AddressLabel;
        AddressBox.Text = draft.Address;
        ScopeBox.Text = draft.Scope;
        AuthorityBox.Text = draft.Authority;
        IssueBox.Text = draft.IssueDate;
        EnglishNameBox.Text = draft.EnglishName;
        EnglishTypeBox.Text = draft.EnglishType;
        EnglishPersonLabel.Text = draft.EnglishPersonRole;
        EnglishPersonBox.Text = draft.EnglishPersonName;
        EnglishExtraLabel.Text = draft.EnglishExtraLabel;
        EnglishExtraBox.Text = draft.EnglishExtraValue;
        EnglishDateLabel.Text = draft.EnglishDateLabel;
        EnglishDateBox.Text = draft.EnglishDateValue;
        EnglishAddressLabel.Text = draft.EnglishAddressLabel;
        EnglishAddressBox.Text = draft.EnglishAddress;
        EnglishScopeBox.Text = draft.EnglishScope;
        EnglishAuthorityBox.Text = draft.EnglishAuthority;
        EnglishIssueBox.Text = draft.EnglishIssueDate;
        _loading = false;
    }

    private void SaveCurrent()
    {
        if (_current is null || _loading) return;
        _current.CreditCode = CreditBox.Text ?? "";
        _current.Name = NameBox.Text ?? "";
        _current.Type = TypeBox.Text ?? "";
        _current.PersonRole = PersonLabel.Text ?? "";
        _current.PersonName = PersonBox.Text ?? "";
        _current.ExtraLabel = ExtraLabel.Text ?? "";
        _current.ExtraValue = ExtraBox.Text ?? "";
        _current.DateLabel = DateLabel.Text ?? "";
        _current.DateValue = DateBox.Text ?? "";
        _current.AddressLabel = AddressLabel.Text ?? "";
        _current.Address = AddressBox.Text ?? "";
        _current.Scope = ScopeBox.Text ?? "";
        _current.Authority = AuthorityBox.Text ?? "";
        _current.IssueDate = IssueBox.Text ?? "";
        _current.EnglishName = EnglishNameBox.Text ?? "";
        _current.EnglishType = EnglishTypeBox.Text ?? "";
        _current.EnglishPersonRole = EnglishPersonLabel.Text ?? "";
        _current.EnglishPersonName = EnglishPersonBox.Text ?? "";
        _current.EnglishExtraLabel = EnglishExtraLabel.Text ?? "";
        _current.EnglishExtraValue = EnglishExtraBox.Text ?? "";
        _current.EnglishDateLabel = EnglishDateLabel.Text ?? "";
        _current.EnglishDateValue = EnglishDateBox.Text ?? "";
        _current.EnglishAddressLabel = EnglishAddressLabel.Text ?? "";
        _current.EnglishAddress = EnglishAddressBox.Text ?? "";
        _current.EnglishScope = EnglishScopeBox.Text ?? "";
        _current.EnglishAuthority = EnglishAuthorityBox.Text ?? "";
        _current.EnglishIssueDate = EnglishIssueBox.Text ?? "";
    }

    private static BusinessLicenseTranslationOptions CreateOptions()
    {
        var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        return new BusinessLicenseTranslationOptions
        {
            ApiKey = settings.VideoTranslateLlmApiKey,
            BaseUrl = settings.VideoTranslateLlmBaseUrl,
            Model = settings.VideoTranslateLlmModel,
        };
    }

    private static void ClearMachineTranslation(BusinessLicenseDraft draft)
    {
        draft.EnglishName = "";
        draft.EnglishType = "";
        draft.EnglishAddress = "";
        draft.EnglishScope = "";
        draft.EnglishAuthority = "";
    }

    private static void CopyParsed(BusinessLicenseDraft target, BusinessLicenseDraft parsed)
    {
        target.Kind = parsed.Kind;
        target.CreditCode = parsed.CreditCode;
        target.Name = parsed.Name;
        target.Type = parsed.Type;
        target.PersonRole = parsed.PersonRole;
        target.PersonName = parsed.PersonName;
        target.ExtraLabel = parsed.ExtraLabel;
        target.ExtraValue = parsed.ExtraValue;
        target.DateLabel = parsed.DateLabel;
        target.DateValue = parsed.DateValue;
        target.AddressLabel = parsed.AddressLabel;
        target.Address = parsed.Address;
        target.Scope = parsed.Scope;
        target.Authority = parsed.Authority;
        target.IssueDate = parsed.IssueDate;
        target.DuplicateNo = parsed.DuplicateNo;
    }
}
