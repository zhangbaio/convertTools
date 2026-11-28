using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokAccountBatchImportDialog : Window
{
    private TikTokAccountBatchImporter? _importer;
    private Action<TikTokAccountImportResult>? _onImported;
    private TikTokPublishAccountBatchRunner? _publishAccountRunner;
    private bool _showingResults;
    private readonly Dictionary<int, string> _manualPaths = [];
    private readonly Dictionary<int, TextBox> _pathEditors = [];

    private sealed record DeclarantChoice(string Company, string SealPath);

    public TikTokAccountBatchImportDialog()
    {
        InitializeComponent();
        MarketBox.SelectionChanged += (_, _) =>
        {
            var local = SelectedTag(MarketBox) == TikTokAccountProfile.DramaMarketLocal;
            EnglishBox.IsEnabled = local;
            LanguageBox.SelectedIndex = local ? 1 : 2;
        };
        InputText.TextChanged += (_, _) => _manualPaths.Clear();
    }

    internal void Bind(TikTokAccountBatchImporter importer, VideoDedupTemplateSnapshot templates,
        IReadOnlyList<TikTokAccountProfile> accounts, Action<TikTokAccountImportResult> onImported,
        TikTokPublishAccountBatchRunner? publishAccountRunner)
    {
        _importer = importer;
        _onImported = onImported;
        _publishAccountRunner = publishAccountRunner;
        CreatePublishAccounts.IsEnabled = publishAccountRunner is not null;
        var templateItems = new List<ComboBoxItem>
        {
            new() { Content = "跟随剧集类型默认模板", Tag = VideoDedupTemplateCatalog.FollowDefaultId },
        };
        templateItems.AddRange(templates.Templates.Select(template => new ComboBoxItem { Content = template.Name, Tag = template.Id }));
        TemplateBox.ItemsSource = templateItems;
        TemplateBox.SelectedIndex = 0;
        var choices = accounts.Where(account => !string.IsNullOrWhiteSpace(account.TiktokProofDeclarantCompanyName))
            .Select(account => new DeclarantChoice(account.TiktokProofDeclarantCompanyName.Trim(), account.TiktokProofSealPath.Trim()))
            .Distinct()
            .OrderBy(choice => choice.Company, StringComparer.CurrentCulture)
            .Select(choice => new ComboBoxItem
            {
                Content = choice.SealPath.Length == 0 ? choice.Company : $"{choice.Company} · {Path.GetFileName(choice.SealPath)}",
                Tag = choice,
            });
        DeclarantPicker.ItemsSource = new[] { new ComboBoxItem { Content = "手动填写" } }.Concat(choices).ToArray();
        DeclarantPicker.SelectedIndex = 0;
    }

    private void OnDeclarantChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((DeclarantPicker.SelectedItem as ComboBoxItem)?.Tag is not DeclarantChoice choice) return;
        DeclarantBox.Text = choice.Company;
        SealBox.Text = choice.SealPath;
    }

    private void OnWorkspaceModeChanged(object? sender, RoutedEventArgs e) =>
        WorkspaceRootPanel.IsVisible = AutoWorkspace.IsChecked == true;

    private async void OnChooseRoot(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择批量工作目录的父目录" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            WorkspaceRootBox.Text = path;
    }

    private async void OnChooseSeal(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择公司印章图片",
            FileTypeFilter = [new FilePickerFileType("印章图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            SealBox.Text = path;
    }

    private async void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_importer is null) return;
        if (_showingResults)
        {
            Close();
            return;
        }

        if (!RowsPane.IsVisible)
        {
            ShowPreview();
            return;
        }

        CaptureManualPaths();
        var finalPlan = CreatePreview();
        var ready = finalPlan.Rows.Where(row => row.Status == TikTokAccountImportStatus.Ready).ToArray();
        if (CreatePublishAccounts.IsChecked != true || _publishAccountRunner is null || ready.Length == 0)
        {
            ShowImportResult(ImportAccounts(finalPlan));
            return;
        }

        TikTokAccountImportResult? result = null;
        var targets = ready.Select(row =>
            new TikTokPublishAccountCreateDialog.Target(row.Number, $"{row.Company} · {row.MaskedEmail}")).ToArray();
        var dialog = new TikTokPublishAccountCreateDialog();
        dialog.Bind(_publishAccountRunner, targets, () =>
        {
            result = ImportAccounts(finalPlan);
            return result.Rows.Where(row => row.Status == TikTokAccountImportStatus.Succeeded)
                .Zip(result.Accounts).ToDictionary(pair => pair.First.Number, pair => pair.Second);
        }, alsoImportsMainAccounts: true);
        await dialog.ShowDialog(this);
        if (result is not null)
            ShowImportResult(result);
    }

    private TikTokAccountImportResult ImportAccounts(TikTokAccountImportPlan plan)
    {
        var result = _importer!.Import(plan);
        _onImported?.Invoke(result);
        return result;
    }

    private void ShowImportResult(TikTokAccountImportResult result)
    {
        _manualPaths.Clear();
        InputText.Text = "";
        _showingResults = true;
        StageHeading.Text = "批量添加结果";
        PreviewSettings.IsVisible = false;
        ShowRows(result.Rows, showEditors: false);
        StatusText.Text = Describe(result.Rows, preview: false);
        BackButton.IsVisible = false;
        NextButton.Content = "完成";
    }

    private TikTokAccountImportPlan CreatePreview() =>
        _importer!.Preview(InputText.Text ?? "", new TikTokAccountImportOptions(
            SelectedTag(MarketBox, TikTokAccountProfile.DramaMarketLocal), EnglishBox.IsChecked == true,
            SelectedTag(LanguageBox), SelectedTag(TemplateBox, VideoDedupTemplateCatalog.FollowDefaultId),
            DeclarantBox.Text ?? "", SealBox.Text ?? "", AutoWorkspace.IsChecked == true, WorkspaceRootBox.Text ?? ""), _manualPaths);

    private void ShowPreview()
    {
        var preview = CreatePreview();
        if (preview.Rows.Count == 0)
        {
            StatusText.Text = "请先粘贴账号文本。";
            InputText.Focus();
            return;
        }
        StageHeading.Text = "导入预览";
        var template = (TemplateBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "跟随剧集类型默认模板";
        var company = string.IsNullOrWhiteSpace(DeclarantBox.Text) ? "留空" : DeclarantBox.Text.Trim();
        var seal = string.IsNullOrWhiteSpace(SealBox.Text) ? "未选择" : Path.GetFileName(SealBox.Text.Trim());
        PreviewSettings.Text = $"去重模板 · {template}　 本公司／声明人 · {company}　 印章 · {seal}";
        ShowRows(preview.Rows, showEditors: true);
        StatusText.Text = Describe(preview.Rows, preview: true)
            + "。账号昵称和致贵方名称均取各行公司名。";
        InputPane.IsVisible = false;
        RowsPane.IsVisible = true;
        BackButton.IsVisible = true;
        NextButton.Content = CreatePublishAccounts.IsChecked == true ? "选择发布账号素材" :
            preview.Rows.Any(row => row.Status == TikTokAccountImportStatus.Ready) ||
            AutoWorkspace.IsChecked != true ? "导入可用账号" : "查看结果";
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        CaptureManualPaths();
        _pathEditors.Clear();
        RowsPanel.Children.Clear();
        StageHeading.Text = "批量添加账号";
        RowsPane.IsVisible = false;
        InputPane.IsVisible = true;
        BackButton.IsVisible = false;
        NextButton.Content = "预览";
        StatusText.Text = "";
    }

    private void CaptureManualPaths()
    {
        foreach (var (number, editor) in _pathEditors)
            _manualPaths[number] = editor.Text ?? "";
    }

    private void ShowRows(IReadOnlyList<TikTokAccountImportRow> rows, bool showEditors)
    {
        _pathEditors.Clear();
        RowsPanel.Children.Clear();
        foreach (var row in rows)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,1.2*,2*,70,2*"), ColumnSpacing = 8 };
            AddCell(line, 0, Text($"{row.Number}. {row.Company}"));
            AddCell(line, 1, Text(row.MaskedEmail));
            if (showEditors && AutoWorkspace.IsChecked != true && row.Status != TikTokAccountImportStatus.Skipped)
            {
                var editor = new TextBox { Text = _manualPaths.GetValueOrDefault(row.Number, row.Workspace), Watermark = "可留空" };
                _pathEditors[row.Number] = editor;
                AddCell(line, 2, editor);
            }
            else
                AddCell(line, 2, Text(row.Workspace.Length == 0 ? "—" : row.Workspace));
            AddCell(line, 3, Text(LabelOf(row.Status)));
            AddCell(line, 4, Text(row.Reason));
            RowsPanel.Children.Add(new Border
            {
                BorderBrush = Brush.Parse("#DFE6EE"), BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(5), Child = line,
            });
        }
    }

    private static string Describe(IReadOnlyList<TikTokAccountImportRow> rows, bool preview)
    {
        var count = rows.Count(row => row.Status == (preview ? TikTokAccountImportStatus.Ready : TikTokAccountImportStatus.Succeeded));
        return $"{(preview ? "可导入" : "成功")} {count} 条 · 跳过 {rows.Count(row => row.Status == TikTokAccountImportStatus.Skipped)} 条 · "
               + $"失败 {rows.Count(row => row.Status == TikTokAccountImportStatus.Failed)} 条";
    }

    private static string LabelOf(TikTokAccountImportStatus status) => status switch
    {
        TikTokAccountImportStatus.Ready => "可导入",
        TikTokAccountImportStatus.Succeeded => "成功",
        TikTokAccountImportStatus.Skipped => "跳过",
        _ => "失败",
    };

    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private static void AddCell(Grid row, int column, Control cell)
    {
        Grid.SetColumn(cell, column);
        row.Children.Add(cell);
    }

    private static string SelectedTag(ComboBox picker, string fallback = "") =>
        (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
}
