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
        AllowDuplicatePublish: false,
        MaxParallelAccounts: 2);

    public string LoadLastRootDir()
    {
        var state = LoadState();
        return state?.LastRootDir ?? string.Empty;
    }

    public void SaveLastRootDir(string rootDir)
    {
        var normalizedRootDir = NormalizeRootDir(rootDir);
        var state = LoadState() ?? new DesktopState(string.Empty, null, null, null);
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

        var currentState = LoadState() ?? new DesktopState(string.Empty, null, null, null);
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

        return savedState with
        {
            MaxParallelAccounts = NormalizeParallelAccounts(savedState.MaxParallelAccounts)
        };
    }

    public void SaveMaterialUploadPageState(string rootDir, MaterialUploadPageState pageState)
    {
        var normalizedRootDir = NormalizeRootDir(rootDir);
        if (string.IsNullOrWhiteSpace(normalizedRootDir))
        {
            return;
        }

        var currentState = LoadState() ?? new DesktopState(string.Empty, null, null, null);
        var pageStates = currentState.MaterialUploadPageStates is null
            ? new Dictionary<string, MaterialUploadPageState>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MaterialUploadPageState>(currentState.MaterialUploadPageStates, StringComparer.OrdinalIgnoreCase);

        pageStates[normalizedRootDir] = pageState;
        SaveState(currentState with { MaterialUploadPageStates = pageStates });
    }

    public MaterialUploadAccountsState LoadMaterialUploadAccountsState()
    {
        var state = LoadState();
        var saved = state?.MaterialUploadAccounts;
        if (saved?.Profiles is { Length: > 0 })
        {
            var profiles = saved.Profiles
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(NormalizeAccountState)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var activeId = profiles.Any(item => string.Equals(item.Id, saved.ActiveAccountProfileId, StringComparison.OrdinalIgnoreCase))
                ? saved.ActiveAccountProfileId
                : profiles.FirstOrDefault()?.Id ?? string.Empty;
            return new MaterialUploadAccountsState(activeId, profiles);
        }

        var discovered = DiscoverMaterialUploadAccounts().ToArray();
        return new MaterialUploadAccountsState(discovered.FirstOrDefault()?.Id ?? string.Empty, discovered);
    }

    public void SaveMaterialUploadAccountsState(MaterialUploadAccountsState accountsState)
    {
        var profiles = (accountsState.Profiles ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(NormalizeAccountState)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var activeId = profiles.Any(item => string.Equals(item.Id, accountsState.ActiveAccountProfileId, StringComparison.OrdinalIgnoreCase))
            ? accountsState.ActiveAccountProfileId
            : profiles.FirstOrDefault()?.Id ?? string.Empty;

        var currentState = LoadState() ?? new DesktopState(string.Empty, null, null, null);
        SaveState(currentState with
        {
            MaterialUploadAccounts = new MaterialUploadAccountsState(activeId, profiles)
        });
    }

    public static MaterialUploadAccountState CreateMaterialUploadAccount(string name, IEnumerable<string> existingIds)
    {
        var id = CreateAccountProfileId(name, existingIds);
        var root = MaterialUploadAccountRoot(id);
        Directory.CreateDirectory(root);
        return new MaterialUploadAccountState(
            id,
            string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
            Path.Combine(root, "wx_auth_state.json"),
            Path.Combine(root, "chromium-profile"));
    }

    public static string MaterialUploadProfilesRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".weixin_channel_tool",
            "profiles");
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
        bool AllowDuplicatePublish,
        int MaxParallelAccounts);

    public sealed record MaterialUploadAccountsState(
        string ActiveAccountProfileId,
        MaterialUploadAccountState[] Profiles);

    public sealed record MaterialUploadAccountState(
        string Id,
        string Name,
        string AuthFile,
        string BrowserProfileDir);

    private sealed record DesktopState(
        string LastRootDir,
        Dictionary<string, ProjectSelectionState>? ProjectSelections,
        Dictionary<string, MaterialUploadPageState>? MaterialUploadPageStates,
        MaterialUploadAccountsState? MaterialUploadAccounts);

    private sealed record ProjectSelectionState(string[] CheckedProjectKeys);

    private static int NormalizeParallelAccounts(int value) => Math.Clamp(value <= 0 ? 2 : value, 1, 8);

    private static IEnumerable<MaterialUploadAccountState> DiscoverMaterialUploadAccounts()
    {
        var profilesRoot = MaterialUploadProfilesRoot();
        if (!Directory.Exists(profilesRoot))
        {
            yield break;
        }

        foreach (var profileDir in Directory.EnumerateDirectories(profilesRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(profileDir);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var authFile = Path.Combine(profileDir, "wx_auth_state.json");
            if (!File.Exists(authFile))
            {
                continue;
            }

            yield return new MaterialUploadAccountState(
                id,
                id,
                authFile,
                Path.Combine(profileDir, "chromium-profile"));
        }
    }

    private static MaterialUploadAccountState NormalizeAccountState(MaterialUploadAccountState state)
    {
        var id = NormalizeAccountProfileId(state.Id, "profile");
        var root = MaterialUploadAccountRoot(id);
        var authFile = string.IsNullOrWhiteSpace(state.AuthFile)
            ? Path.Combine(root, "wx_auth_state.json")
            : state.AuthFile.Trim();
        var browserProfileDir = string.IsNullOrWhiteSpace(state.BrowserProfileDir)
            ? Path.Combine(root, "chromium-profile")
            : state.BrowserProfileDir.Trim();
        return state with
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(state.Name) ? id : state.Name.Trim(),
            AuthFile = authFile,
            BrowserProfileDir = browserProfileDir
        };
    }

    private static string CreateAccountProfileId(string name, IEnumerable<string> existingIds)
    {
        var existing = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseId = NormalizeAccountProfileId(name, "profile").ToLowerInvariant();
        var id = baseId;
        var suffix = 2;
        while (existing.Contains(id))
        {
            id = $"{baseId}-{suffix++}";
        }

        return id;
    }

    private static string MaterialUploadAccountRoot(string id) => Path.Combine(MaterialUploadProfilesRoot(), id);

    private static string NormalizeAccountProfileId(string? value, string fallback)
    {
        var chars = (value ?? string.Empty)
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-', '_');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
