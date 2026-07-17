namespace ShortDrama.Core.Models;

public sealed record VideoTranscodeRequest(
    string ProjectDir,
    string InputDir,
    string OutputDir,
    string? ConfigFile = null,
    bool Overwrite = false,
    int Crf = 23,
    string Preset = "fast");
