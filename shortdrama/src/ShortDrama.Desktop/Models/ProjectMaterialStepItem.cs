using Avalonia.Media;

namespace ShortDrama.Desktop.Models;

public sealed record ProjectMaterialStepItem(
    int Index,
    string Key,
    string Label,
    string Status,
    string Summary,
    bool IsSelected,
    bool ShowConnector,
    IBrush BackgroundBrush,
    IBrush AccentBrush,
    IBrush TitleBrush,
    IBrush SummaryBrush);
