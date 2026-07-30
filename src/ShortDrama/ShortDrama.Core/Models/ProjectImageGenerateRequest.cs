using System.Text.Json.Serialization;

namespace ShortDrama.Core.Models;

public sealed record ProjectImageGenerateRequest(
    string ProjectDir,
    string InputDir,
    string OutputDir,
    string? TemplateImageDir = null,
    string? ConfigFile = null,
    int? Count = null,
    bool Overwrite = false,
    IReadOnlyList<string>? EpisodeNames = null,
    IReadOnlyList<string>? SourceVideos = null,
    [property: JsonIgnore] Action<string>? Progress = null);
