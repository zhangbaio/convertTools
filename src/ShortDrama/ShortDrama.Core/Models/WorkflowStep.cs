namespace ShortDrama.Core.Models;

public sealed record WorkflowStep(
    string Type,
    string? Template,
    string? ConfigFile,
    string? OutputFile,
    string? InputDir,
    string? OutputDir,
    string? NameTemplate = null,
    int? Count = null,
    bool? Overwrite = null,
    int? Crf = null,
    string? Preset = null,
    bool ContinueOnError = false,
    int Retry = 0,
    int? TimeoutSeconds = null);
