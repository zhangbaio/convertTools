using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.Services;

namespace ShortDrama.Desktop.Views;

public partial class MaterialSystemHighlightBatchPublishWindow : Window
{
    public MaterialSystemHighlightBatchPublishWindow()
    {
        InitializeComponent();

        DefaultDescriptionTextBox.Text = MaterialSystemHighlightBatchPublishService.DefaultDescription;
        StartButton.Click += StartButton_Click;
        CancelButton.Click += (_, _) => Close(null);
        PublishByCountRadioButton.IsCheckedChanged += (_, _) => RefreshState();
        PublishByTypeRadioButton.IsCheckedChanged += (_, _) => RefreshState();
        RegenerateAfterPublishCheckBox.IsCheckedChanged += (_, _) => RefreshState();
        RefreshState();
    }

    public MaterialSystemHighlightBatchPublishDialogResult? Result { get; private set; }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        var titles = TitlesTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(titles))
        {
            ValidationMessageTextBlock.Text = "请至少填写一个剧名。";
            TitlesTextBox.Focus();
            return;
        }

        Result = new MaterialSystemHighlightBatchPublishDialogResult(
            TitlesText: titles,
            DefaultDescription: string.IsNullOrWhiteSpace(DefaultDescriptionTextBox.Text)
                ? MaterialSystemHighlightBatchPublishService.DefaultDescription
                : DefaultDescriptionTextBox.Text.Trim(),
            PublishCount: Math.Max(1, (int)(PublishCountUpDown.Value ?? 10)),
            PublishTargetMode: PublishByTypeRadioButton.IsChecked == true ? "type" : "count",
            PublishVideoTypes: CheckedTypes(PublishMashupCheckBox, PublishCommentaryCheckBox, PublishSliceCheckBox),
            RegenerateAfterPublish: RegenerateAfterPublishCheckBox.IsChecked == true,
            RegenerateVideoTypes: CheckedTypes(RegenerateMashupCheckBox, RegenerateCommentaryCheckBox, RegenerateSliceCheckBox));
        Close(Result);
    }

    private void RefreshState()
    {
        var publishByType = PublishByTypeRadioButton.IsChecked == true;
        PublishCountUpDown.IsEnabled = !publishByType;
        PublishMashupCheckBox.IsEnabled = publishByType;
        PublishCommentaryCheckBox.IsEnabled = publishByType;
        PublishSliceCheckBox.IsEnabled = publishByType;

        var regenerate = RegenerateAfterPublishCheckBox.IsChecked == true;
        RegenerateMashupCheckBox.IsEnabled = regenerate;
        RegenerateCommentaryCheckBox.IsEnabled = regenerate;
        RegenerateSliceCheckBox.IsEnabled = regenerate;
    }

    private static IReadOnlyList<string> CheckedTypes(params CheckBox[] checkBoxes)
    {
        var selected = checkBoxes
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => checkBox.Content?.ToString() ?? string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        return selected.Length == 0 ? MaterialSystemHighlightBatchPublishService.VideoTypeOptions : selected;
    }
}

public sealed record MaterialSystemHighlightBatchPublishDialogResult(
    string TitlesText,
    string DefaultDescription,
    int PublishCount,
    string PublishTargetMode,
    IReadOnlyList<string> PublishVideoTypes,
    bool RegenerateAfterPublish,
    IReadOnlyList<string> RegenerateVideoTypes);
