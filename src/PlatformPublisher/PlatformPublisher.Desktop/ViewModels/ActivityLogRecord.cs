using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed class ActivityLogRecord
{
    public string Text { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public PublishPlatform Platform { get; init; }
}

public sealed class KuaishouLogProject
{
    public KuaishouLogProject(string projectName, string projectDirectory, string tone)
    {
        ProjectName = projectName;
        ProjectDirectory = projectDirectory;
        Tone = tone;
    }

    public const int MaxLines = 1000;

    public string ProjectName { get; }
    public string ProjectDirectory { get; }
    public string Tone { get; set; }
    private readonly List<string> _lines = [];

    public void AppendLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        _lines.Add(text);
        if (_lines.Count > MaxLines)
            _lines.RemoveRange(0, _lines.Count - MaxLines);
    }

    public IReadOnlyList<string> CopyLines() => _lines.ToArray();

    public static List<KuaishouLogProject> RetainFailures(
        IReadOnlyList<KuaishouLogProject>? previous,
        IReadOnlyList<KuaishouLogProject> current)
    {
        var merged = new List<KuaishouLogProject>(current);
        if (previous is null)
            return merged;

        foreach (var old in previous)
        {
            if (!string.Equals(old.Tone, "failed", StringComparison.Ordinal))
                continue;
            if (merged.Any(item =>
                    string.Equals(item.ProjectDirectory, old.ProjectDirectory, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.ProjectName, old.ProjectName, StringComparison.Ordinal)))
                continue;
            merged.Add(old);
        }

        return merged;
    }
}
