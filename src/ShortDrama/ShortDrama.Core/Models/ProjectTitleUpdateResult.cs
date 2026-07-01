namespace ShortDrama.Core.Models;

public sealed record ProjectTitleUpdateResult(
    string ProjectKey,
    string OriginalTitle,
    string NewTitle,
    string WorkflowProjectDir,
    int RenamedVideoCount,
    int UpdatedWeixinConfigCount,
    IReadOnlyList<string> RegeneratedSteps,
    IReadOnlyList<string> InvalidatedSteps);
