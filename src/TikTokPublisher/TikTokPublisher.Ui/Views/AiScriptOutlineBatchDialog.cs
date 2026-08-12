using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed record AiScriptOutlineBatchDialogResult(
    IReadOnlyList<CopyrightProofProjectMatch> Matches,
    bool ForceRegenerate,
    CopyrightProofExecutionMode ExecutionMode);

public sealed class AiScriptOutlineBatchDialog : Window
{
    private readonly IReadOnlyList<QueueProjectItem> _projects;
    private readonly IReadOnlyList<ArchivedProjectItem> _archives;
    private readonly TextBox _input;
    private readonly StackPanel _preview;
    private readonly TextBlock _summary;
    private readonly CheckBox _force;
    private readonly RadioButton _generateOnly;
    private readonly RadioButton _generateAndEdit;
    private IReadOnlyList<CopyrightProofProjectMatch> _matched = [];

    private AiScriptOutlineBatchDialog(
        IReadOnlyList<QueueProjectItem> projects,
        IReadOnlyList<ArchivedProjectItem> archives,
        string? initialInput)
    {
        _projects = projects;
        _archives = archives;
        Title = "补全 AI 剧本大纲";
        Width = 820;
        MinWidth = 680;
        MinHeight = 390;
        MaxHeight = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,150,Auto,Auto,Auto,Auto"),
            RowSpacing = 10,
        };
        root.Children.Add(new TextBlock
        {
            Text = "输入新剧名，一行一个。只按新剧名精确匹配当前工作目录及已归档项目；归档项目会先回退再执行。找不到或存在同名冲突的项目将自动跳过。生成文件固定为 AI剧本大纲.pdf。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        var modeGroup = $"ai-outline-mode-{Guid.NewGuid():N}";
        _generateOnly = new RadioButton
        {
            Content = "只生成产物",
            GroupName = modeGroup,
        };
        _generateAndEdit = new RadioButton
        {
            Content = "生成并编辑 TikTok",
            GroupName = modeGroup,
            IsChecked = true,
        };
        var modePanel = new StackPanel { Spacing = 5 };
        modePanel.Children.Add(new TextBlock { Text = "执行模式", FontWeight = FontWeight.SemiBold });
        var modeChoices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        modeChoices.Children.Add(_generateOnly);
        modeChoices.Children.Add(_generateAndEdit);
        modePanel.Children.Add(modeChoices);
        modePanel.Children.Add(new TextBlock
        {
            Text = "“生成并编辑 TikTok”会在生成大纲后进入版权证明页，将 PDF 补充到 AI 生成过程截图材料中。",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetRow(modePanel, 1);
        root.Children.Add(modePanel);

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "每行输入一个新剧名",
            Text = initialInput ?? string.Empty,
        };
        Grid.SetRow(_input, 2);
        root.Children.Add(_input);

        var previewHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        previewHeader.Children.Add(_summary);
        var refresh = new Button { Content = "重新匹配", MinWidth = 96 };
        refresh.Click += (_, _) => RefreshPreview();
        Grid.SetColumn(refresh, 1);
        previewHeader.Children.Add(refresh);
        Grid.SetRow(previewHeader, 3);
        root.Children.Add(previewHeader);

        _preview = new StackPanel { Spacing = 6 };
        var scroller = new ScrollViewer
        {
            Content = _preview,
            MaxHeight = 220,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 4);
        root.Children.Add(scroller);

        _force = new CheckBox { Content = "强制重新生成已有的 AI剧本大纲.pdf" };
        Grid.SetRow(_force, 5);
        root.Children.Add(_force);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var execute = new Button { Content = "开始补全", MinWidth = 108 };
        execute.Click += (_, _) =>
        {
            try
            {
                RefreshPreview();
            }
            catch (Exception ex)
            {
                _summary.Text = $"匹配项目失败：{ex.Message}";
                _summary.Foreground = Brushes.IndianRed;
                return;
            }
            if (_matched.Count == 0)
            {
                _summary.Text = "没有匹配到可执行项目。";
                _summary.Foreground = Brushes.IndianRed;
                return;
            }
            Close(new AiScriptOutlineBatchDialogResult(
                _matched,
                _force.IsChecked == true,
                _generateOnly.IsChecked == true
                    ? CopyrightProofExecutionMode.GenerateMaterialOnly
                    : CopyrightProofExecutionMode.GenerateAndEdit));
        };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(execute);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 6);
        root.Children.Add(buttons);

        Content = root;
        Opened += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_input.Text)) RefreshPreview();
            _input.Focus();
        };
    }

    public static Task<AiScriptOutlineBatchDialogResult?> ShowAsync(
        Window owner,
        IReadOnlyList<QueueProjectItem> projects,
        IReadOnlyList<ArchivedProjectItem> archives,
        string? initialInput = null) =>
        new AiScriptOutlineBatchDialog(projects, archives, initialInput)
            .ShowDialog<AiScriptOutlineBatchDialogResult?>(owner);

    private void RefreshPreview()
    {
        var titles = (_input.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var matches = CopyrightProofProjectMatcher.MatchByNewTitleExact(
            titles,
            _projects,
            _archives);
        var matched = new List<CopyrightProofProjectMatch>();
        _preview.Children.Clear();
        foreach (var match in matches)
        {
            if (match.Location == CopyrightProofProjectLocation.Missing)
            {
                AddPreview(match.NewTitle, "未找到，跳过", Brushes.Gray);
            }
            else if (match.Location == CopyrightProofProjectLocation.Conflict)
            {
                AddPreview(match.NewTitle, "同名冲突，跳过", Brushes.IndianRed);
            }
            else
            {
                matched.Add(match);
                if (match.Location == CopyrightProofProjectLocation.Archived)
                {
                    AddPreview(match.NewTitle, "已归档，将先回退", Brushes.DarkOrange);
                }
                else
                {
                    var exists = AiOutlineExists(match.QueueProject!);
                    AddPreview(match.NewTitle, exists ? "文件已存在" : "待生成", exists ? Brushes.DarkOrange : Brushes.SeaGreen);
                }
            }
        }
        _matched = matched;
        _summary.Text = $"输入 {titles.Length} 个，匹配 {matched.Count} 个，跳过 {titles.Length - matched.Count} 个。";
        _summary.Foreground = Brushes.Black;
    }

    private static bool AiOutlineExists(QueueProjectItem project)
    {
        try
        {
            var workflow = ProjectWorkspaceService.LoadContext(project.ProjectDir).WorkflowProjectDir;
            return File.Exists(Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName));
        }
        catch
        {
            // This check only affects the preview label. Queue execution performs the full
            // validation and must not be blocked by stale workflow metadata here.
            return false;
        }
    }

    private void AddPreview(string title, string state, IBrush color) =>
        _preview.Children.Add(new TextBlock
        {
            Text = $"• {title}    [{state}]",
            Foreground = color,
            TextWrapping = TextWrapping.Wrap,
        });
}
