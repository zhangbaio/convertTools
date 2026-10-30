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

    public string ProjectName { get; }
    public string ProjectDirectory { get; }
    public string Tone { get; set; }
}
