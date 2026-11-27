using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LicenseTranslate.Auth;
using LicenseTranslate.Core;

namespace LicenseTranslate.App;

public partial class TranslateWindow : Window
{
    private readonly AccountSession _session;
    private readonly DispatcherTimer _verifyTimer;
    private readonly List<BusinessLicenseDraft> _drafts = [];
    private readonly HashSet<BusinessLicenseDraft> _editedDrafts = [];
    private BusinessLicenseDraft? _current;
    private bool _busy;
    private bool _loading;

    public TranslateWindow(AccountSession session)
    {
        _session = session;
        InitializeComponent();
        var settings = ((App)Application.Current!).Settings;
        AccountText.Text = "当前账号：" + (string.IsNullOrWhiteSpace(session.Email) ? session.Account : session.Email);
        LlmUrlBox.Text = settings.LlmBaseUrl;
        LlmModelBox.Text = settings.LlmModel;
        LlmKeyBox.Text = settings.LlmApiKey;
        foreach (var field in LicenseForm.GetLogicalDescendants().OfType<TextBox>())
            field.TextChanging += LicenseField_TextChanging;
        UpdateActions();
        _verifyTimer = new DispatcherTimer { Interval = AccountProduct.VerifyInterval };
        _verifyTimer.Tick += async (_, _) => await VerifyAsync();
        Opened += (_, _) => _verifyTimer.Start();
        Closing += (_, _) => _verifyTimer.Stop();
        Closed += (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && ReferenceEquals(desktop.MainWindow, this))
                desktop.Shutdown();
        };
    }

    private async Task VerifyAsync()
    {
        var app = (App)Application.Current!;
        try
        {
            var verified = await app.Auth.VerifyAsync(app.Store.Load());
            app.Store.Save(verified);
        }
        catch (Exception ex) when (ex is AccountAuthException)
        {
            _verifyTimer.Stop();
            if (ex is AccountRejectedException)
                app.Store.Clear();
            app.ShowLogin(ex.Message, skipRestore: true);
        }
    }

    private async void Logout_Click(object? sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current!;
        _verifyTimer.Stop();
        var session = app.Store.Load();
        app.Store.Clear();
        try { await app.Auth.LogoutAsync(session); }
        catch (AccountAuthException) { }
        app.ShowLogin("已退出登录。", skipRestore: true);
    }

    private void SaveSettings()
    {
        var settings = ((App)Application.Current!).Settings;
        settings.LlmBaseUrl = LlmUrlBox.Text?.Trim() ?? "";
        settings.LlmModel = LlmModelBox.Text?.Trim() ?? "";
        settings.LlmApiKey = LlmKeyBox.Text ?? "";
        settings.Save();
    }

    private BusinessLicenseTranslationOptions TranslationOptions()
    {
        SaveSettings();
        var settings = ((App)Application.Current!).Settings;
        return new BusinessLicenseTranslationOptions
        {
            GlossaryPath = Path.Combine(AppContext.BaseDirectory, "license-translation-glossary.json"),
            BaseUrl = settings.LlmBaseUrl,
            Model = settings.LlmModel,
            ApiKey = settings.LlmApiKey,
        };
    }

    private async void TranslateLicenses_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            await TranslateLicensesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "翻译失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TranslateLicensesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择营业执照图片",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] }],
        });
        if (files.Count == 0) return;
        SaveCurrent();
        var selected = new List<BusinessLicenseDraft>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) continue;
            var draft = _drafts.FirstOrDefault(item => string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase));
            if (draft is null)
            {
                draft = new BusinessLicenseDraft { SourcePath = path };
                _drafts.Add(draft);
            }
            selected.Add(draft);
        }
        if (selected.Count == 0) return;
        BindFiles();
        var options = TranslationOptions();
        var failures = new List<string>();
        for (var i = 0; i < selected.Count; i++)
        {
            var draft = selected[i];
            StatusText.Text = $"正在翻译 {i + 1}/{selected.Count}…";
            try
            {
                var parsed = await BusinessLicenseVision.ReadAsync(draft.SourcePath, options, CancellationToken.None);
                CopyParsed(draft, parsed);
                draft.EnglishName = draft.EnglishType = draft.EnglishAddress = draft.EnglishScope = draft.EnglishAuthority = "";
                BusinessLicenseTranslator.ApplyFixed(draft);
                await BusinessLicenseAiTranslator.TranslateAsync(draft, options);
                _editedDrafts.Remove(draft);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(draft.SourcePath)}：{ex.Message}");
            }
            if (ReferenceEquals(_current, draft) || _current is null)
                Show(draft);
        }
        Show(_current ?? selected[0]);
        StatusText.Text = failures.Count == 0
            ? $"已翻译 {selected.Count} 张执照。请核对英文后再生成。"
            : "翻译失败：" + string.Join(" ", failures);
    }

    private async void ClearLicenses_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _drafts.Count == 0) return;
        if (_editedDrafts.Count > 0 && !await ConfirmClearAsync()) return;

        _drafts.Clear();
        _editedDrafts.Clear();
        BindFiles();
        StatusText.Text = "已清空已选执照。";
    }

    private Task<bool> ConfirmClearAsync()
    {
        var confirm = new Button { Content = "清空" };
        var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true };
        var dialog = new Window
        {
            Title = "清空已选执照",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "清空后会丢弃尚未导出的手动修改。", TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm },
                    },
                },
            },
        };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return dialog.ShowDialog<bool>(this);
    }

    private async void Generate_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SaveCurrent();
        var drafts = _drafts.Where(draft => !string.IsNullOrWhiteSpace(draft.EnglishName)).ToList();
        if (drafts.Count == 0)
        {
            StatusText.Text = "请先翻译执照，并确认英文名称。";
            return;
        }
        await GenerateAsync(drafts, pdfOnly: false);
    }

    private async void GenerateCurrentPdf_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _current is not { } draft) return;
        SaveCurrent();
        if (string.IsNullOrWhiteSpace(draft.EnglishName)) return;
        await GenerateAsync([draft], pdfOnly: true);
    }

    private async Task GenerateAsync(IReadOnlyList<BusinessLicenseDraft> drafts, bool pdfOnly)
    {
        SetBusy(true);
        StatusText.Text = pdfOnly ? "正在生成当前执照的 PDF…" : "正在批量生成 Word 和 PDF…";
        try
        {
            var options = TranslationOptions();
            var written = new List<string>();
            var failures = new List<string>();
            foreach (var draft in drafts)
            {
                try
                {
                    var paths = await Task.Run(() => Export(draft, options.GlossaryPath, pdfOnly));
                    written.AddRange(paths);
                    _editedDrafts.Remove(draft);
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(draft.SourcePath)}：{ex.Message}");
                }
            }
            var success = written.Count > 0 ? "已生成：" + string.Join("；", written) : "";
            StatusText.Text = failures.Count == 0
                ? success
                : (success.Length > 0 ? success + "。" : "") + "生成失败：" + string.Join(" ", failures);
        }
        catch (Exception ex)
        {
            StatusText.Text = "生成失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string[] Export(BusinessLicenseDraft draft, string glossaryPath, bool pdfOnly)
    {
        BusinessLicenseAiTranslator.Remember(draft, glossaryPath);
        if (pdfOnly) return [BusinessLicenseExporter.ExportPdf(draft)];
        var (word, pdf) = BusinessLicenseExporter.ExportWordAndPdf(draft);
        return [word, pdf];
    }

    private void FileBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SaveCurrent();
        var index = FileBox.SelectedIndex;
        Show(index >= 0 && index < _drafts.Count ? _drafts[index] : null);
    }

    private void BindFiles()
    {
        _loading = true;
        FileBox.ItemsSource = _drafts.Select(item => item.DisplayName).ToList();
        FileBox.SelectedIndex = _drafts.Count > 0 ? 0 : -1;
        _loading = false;
        Show(_drafts.Count > 0 ? _drafts[0] : null);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateActions();
    }

    private void UpdateActions()
    {
        TranslateButton.IsEnabled = !_busy;
        ClearLicensesButton.IsEnabled = !_busy && _drafts.Count > 0;
        GenerateBatchButton.IsEnabled = !_busy && _drafts.Count > 0;
        GeneratePdfButton.IsEnabled = !_busy && _current is not null && !string.IsNullOrWhiteSpace(EnglishNameBox.Text);
        FileBox.IsEnabled = !_busy && _drafts.Count > 0;
        LicenseForm.IsEnabled = !_busy && _current is not null;
    }

    private void LicenseField_TextChanging(object? sender, TextChangingEventArgs e)
    {
        if (_loading || _current is null) return;
        _editedDrafts.Add(_current);
        if (ReferenceEquals(sender, EnglishNameBox))
            UpdateActions();
    }

    private void CreditBox_TextChanging(object? sender, TextChangingEventArgs e) => CopyCredit(CreditBox, EnglishCreditBox);

    private void EnglishCreditBox_TextChanging(object? sender, TextChangingEventArgs e) => CopyCredit(EnglishCreditBox, CreditBox);

    private void CopyCredit(TextBox source, TextBox target)
    {
        // 同步另一栏后，切换执照时才能读到最新的信用代码。
        if (_loading || source.Text == target.Text) return;
        _loading = true;
        target.Text = source.Text;
        _loading = false;
    }

    private void Show(BusinessLicenseDraft? draft)
    {
        _current = draft;
        draft ??= new BusinessLicenseDraft();
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
        UpdateActions();
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
