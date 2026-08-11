using System.Text.Json.Serialization;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

/// <summary>
/// One subtitle cue returned by an ASR implementation. Timestamps use milliseconds so
/// callers can pass the original recognizer result without losing precision.
/// </summary>
public sealed record FableCutSubtitleCue(
    double StartMilliseconds,
    double EndMilliseconds,
    string Text);

/// <summary>A project document understood by the FableCut editor.</summary>
public sealed class FableCutProject
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("fps")]
    public int Fps { get; init; } = 30;

    [JsonPropertyName("revision")]
    public int Revision { get; init; } = 1;

    [JsonPropertyName("media")]
    public IReadOnlyList<FableCutMediaItem> Media { get; init; } = [];

    [JsonPropertyName("clips")]
    public IReadOnlyList<FableCutClip> Clips { get; init; } = [];

    [JsonPropertyName("markers")]
    public IReadOnlyList<FableCutMarker> Markers { get; init; } = [];
}

public sealed class FableCutMediaItem
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("src")]
    public string Src { get; init; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}

public sealed class FableCutClip
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("mediaId")]
    public string? MediaId { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("track")]
    public string Track { get; init; } = "";

    [JsonPropertyName("start")]
    public double Start { get; init; }

    [JsonPropertyName("in")]
    public double In { get; init; }

    [JsonPropertyName("duration")]
    public double Duration { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("linkGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkGroup { get; init; }

    [JsonPropertyName("props")]
    public IReadOnlyDictionary<string, object?> Props { get; init; } =
        new Dictionary<string, object?>();
}

/// <summary>
/// FableCut ruler marker. Its persisted schema is deliberately <c>t/label</c>, not
/// the display-oriented <c>time/name</c> aliases used by older integrations.
/// </summary>
public sealed class FableCutMarker
{
    [JsonPropertyName("t")]
    public double Time { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";
}
