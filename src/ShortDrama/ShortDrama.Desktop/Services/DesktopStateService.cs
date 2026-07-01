using System.Text.Json;

namespace ShortDrama.Desktop.Services;

public sealed class DesktopStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly MaterialUploadPageState DefaultMaterialUploadPageState = new(
        GenerateHighlights: true,
        MaterialUploadEnabled: false,
        AllowDuplicatePublish: false);

    public string LoadLastRootDir()
    {
        var state = LoadState();
        return state?.LastRootDir ?? string.Empty;
    }

    public void SaveLastRootDir(string rootDir)
    {
        var normalizedRootDir = NormalizeRootDir(rootDir);
        var state = LoadState() ?? new DesktopState(string.Empty, null, null);
        SaveState(state with { LastRootDir = normalizedRootDir });
    }

    public HashSet<string> LoadCheckedProjectKeys(string rootDir)
    {
        var normalizedRootDir = NormalizeRootDir(rootDir);
        if (string.IsNullOrWhiteSpace(normalizedRootDir))
        {
            return [];
        }

        var state = LoadState();
        if (state?.ProjectSelections is null ||
            !state.ProjectSelections.TryGetValue(normalizedRootDir, out var selection) ||
            selection.CheckedProjectKeys is null)
        {
            return [];
        }

        return selection.CheckedProjectKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .ToHashSet(StringComparer.Ordinal);
    }

    public void SaveCheckedProjectKeys(string rootDir, IEnumerable<string> checkedProjectKeys)
    {
        var normalizedRootDir = NormalizeRootDir(rootDir);
        if (string.IsNullOrWhiteSpace(normalizedRootDir))
        {
            return;
        }

        var normalizedKeys = checkedProjectKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        var currentState = LoadState() ?? new DesktopState(string.Empty, null, null);
        var selections = currentState.ProjectSelections is null
            ? new Dictionary<string, ProjectSelectionState>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ProjectSelectionState>(currentState.ProjectSelections, StringComparer.OrdinalIgnoreCase);

        if (normalizedKeys.Length == 0)
        {
            selections.Remove(normalizedRootDir);
        }
        else
        {
            selections[normalizedRootDir] = new ProjectSelectionState(normalizedKeys);
        }

        SaveState(currentState with { ProjectSelections = selections });
    }

    public MaterialUploadPageState LoadMaterialUploadPageState(string rootDir)
    {
        var normalizedRootDir = NormalizeRootDir(rootDir);
        if (string.IsNullOrWhiteSpace(normalizedRootDir))
        {
            return DefaultMaterialUploadPageState;
        }

        var state = LoadState();
        if (state?.MaterialUploadPageStates is null ||
            !state.MaterialUploadPageStates.TryGetValue(normalizedRootDir, out var savedState) ||
            savedState is null)
        {
            return DefaultMaterialUploadPageState;
        }

        return savedState;
    }

    public void SaveMaterialUploadPageState(string rootDir, MaterialUploadPageState pageState)
    {
        var normalizedRootDir = NormalizeRootDir(rootDir);
        if (string.IsNullOrWhiteSpace(normalizedRootDir))
        {
            return;
        }

        var currentState = LoadState() ?? new DesktopState(string.Empty, null, null);
        var pageStates = currentState.MaterialUploadPageStates is null
            ? new Dictionary<string, MaterialUploadPageState>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MaterialUploadPageState>(currentState.MaterialUploadPageStates, StringComparer.OrdinalIgnoreCase);

        pageStates[normalizedRootDir] = pageState;
        SaveState(currentState with { MaterialUploadPageStates = pageStates });
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

    private static void SaveState(DesktopState state)
    {
        var statePath = GetStateFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static string NormalizeRootDir(string? rootDir)
    {
        var trimmed = rootDir?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch
        {
            // Keep the original path when normalization cannot resolve it yet.
        }

        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string GetStateFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".shortdrama-desktop")
            : Path.Combine(appData, "ShortDramaDesktop");

        return Path.Combine(baseDir, "state.json");
    }

    public sealed record MaterialUploadPageState(
        bool GenerateHighlights,
        bool MaterialUploadEnabled,
        bool AllowDuplicatePublish);

    private sealed record DesktopState(
        string LastRootDir,
        Dictionary<string, ProjectSelectionState>? ProjectSelections,
        Dictionary<string, MaterialUploadPageState>? MaterialUploadPageStates);

    private sealed record ProjectSelectionState(string[] CheckedProjectKeys);
}
