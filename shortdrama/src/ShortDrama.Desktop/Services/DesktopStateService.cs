using System.Text.Json;

namespace ShortDrama.Desktop.Services;

public sealed class DesktopStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string LoadLastRootDir()
    {
        var state = LoadState();
        return state?.LastRootDir ?? string.Empty;
    }

    public void SaveLastRootDir(string rootDir)
    {
        var statePath = GetStateFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        var state = new DesktopState(rootDir?.Trim() ?? string.Empty);
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static DesktopState? LoadState()
    {
        var statePath = GetStateFilePath();
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(statePath);
            return JsonSerializer.Deserialize<DesktopState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string GetStateFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".shortdrama-desktop")
            : Path.Combine(appData, "ShortDramaDesktop");

        return Path.Combine(baseDir, "state.json");
    }

    private sealed record DesktopState(string LastRootDir);
}
