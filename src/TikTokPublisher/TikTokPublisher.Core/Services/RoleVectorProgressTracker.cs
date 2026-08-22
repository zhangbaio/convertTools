using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TikTokPublisher.Core.Services;

public sealed class RoleVectorProgressTracker
{
    internal const string FileName = ".role-vector-progress.json";
    internal const string Version = "v1";
    private readonly object _lock = new();
    private readonly string _path;
    private RoleVectorProgressState _state;

    private RoleVectorProgressTracker(string path, RoleVectorProgressState state)
    {
        _path = path;
        _state = state;
    }

    internal static RoleVectorProgressTracker Open(
        string workflowProjectDirectory,
        string requestFingerprint,
        bool forceRerun,
        Action<string>? log)
    {
        var path = Path.Combine(
            TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory),
            FileName);
        RoleVectorProgressState? existing = null;
        if (!forceRerun && File.Exists(path))
        {
            try
            {
                existing = JsonSerializer.Deserialize<RoleVectorProgressState>(File.ReadAllText(path));
            }
            catch
            {
                existing = null;
            }
        }
        if (existing is not null &&
            existing.Version == Version &&
            string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            var tracker = new RoleVectorProgressTracker(path, existing);
            log?.Invoke(
                $"角色矢量图：恢复上次进度，阶段={existing.Phase}；" +
                $"已审核剧集 {existing.CheckedEpisodes.Count} 集，" +
                $"已选参考人物 {tracker.GetSelectedSources().Count} 人，" +
                $"已完成角色图 {existing.Characters.Count} 张。");
            return tracker;
        }

        var state = new RoleVectorProgressState
        {
            Version = Version,
            RequestFingerprint = requestFingerprint,
            Phase = "initialized",
            UpdatedAt = DateTimeOffset.Now,
        };
        var created = new RoleVectorProgressTracker(path, state);
        created.Save();
        return created;
    }

    internal static void Clear(string workflowProjectDirectory)
    {
        var path = Path.Combine(
            TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory),
            FileName);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    internal IReadOnlySet<int> CheckedEpisodes
    {
        get
        {
            lock (_lock) return _state.CheckedEpisodes.ToHashSet();
        }
    }

    internal IReadOnlyList<string> GetSelectedSources()
    {
        lock (_lock)
        {
            return _state.SelectedSources
                .Where(entry => File.Exists(entry.Path) && MatchesHash(entry.Path, entry.Sha256))
                .Select(entry => Path.GetFullPath(entry.Path))
                .ToArray();
        }
    }

    internal void MarkVisionBatch(
        IEnumerable<int> episodes,
        IReadOnlyList<string> selectedSources)
    {
        lock (_lock)
        {
            foreach (var episode in episodes.Where(value => value > 0))
                _state.CheckedEpisodes.Add(episode);
            _state.SelectedSources = selectedSources
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new RoleVectorProgressFile
                {
                    Path = Path.GetFullPath(path),
                    Sha256 = ComputeSha256(path),
                })
                .ToList();
            _state.Phase = "vision_review";
            SaveLocked();
        }
    }

    internal bool CanReuseCharacter(string roleName, string referencePath, string outputPath)
    {
        lock (_lock)
        {
            if (!_state.Characters.TryGetValue(roleName, out var entry) || !File.Exists(outputPath)) return false;
            var referenceHash = File.Exists(referencePath) ? ComputeSha256(referencePath) : string.Empty;
            return string.Equals(entry.ReferenceSha256, referenceHash, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(entry.OutputPath, Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase) &&
                   MatchesHash(outputPath, entry.OutputSha256);
        }
    }

    internal void MarkCharacter(string roleName, string referencePath, string outputPath)
    {
        lock (_lock)
        {
            _state.Characters[roleName] = new RoleVectorProgressCharacter
            {
                ReferencePath = string.IsNullOrWhiteSpace(referencePath) ? string.Empty : Path.GetFullPath(referencePath),
                ReferenceSha256 = File.Exists(referencePath) ? ComputeSha256(referencePath) : string.Empty,
                OutputPath = Path.GetFullPath(outputPath),
                OutputSha256 = ComputeSha256(outputPath),
            };
            _state.Phase = "character_generation";
            SaveLocked();
        }
    }

    internal void MarkPhase(string phase)
    {
        lock (_lock)
        {
            _state.Phase = phase;
            SaveLocked();
        }
    }

    internal void Complete()
    {
        lock (_lock)
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }

    private void Save()
    {
        lock (_lock) SaveLocked();
    }

    private void SaveLocked()
    {
        _state.UpdatedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool MatchesHash(string path, string expected) =>
        !string.IsNullOrWhiteSpace(expected) && File.Exists(path) &&
        string.Equals(ComputeSha256(path), expected, StringComparison.OrdinalIgnoreCase);

    private sealed class RoleVectorProgressState
    {
        public string Version { get; set; } = RoleVectorProgressTracker.Version;
        public string RequestFingerprint { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public HashSet<int> CheckedEpisodes { get; set; } = [];
        public List<RoleVectorProgressFile> SelectedSources { get; set; } = [];
        public Dictionary<string, RoleVectorProgressCharacter> Characters { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class RoleVectorProgressFile
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class RoleVectorProgressCharacter
    {
        public string ReferencePath { get; set; } = string.Empty;
        public string ReferenceSha256 { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string OutputSha256 { get; set; } = string.Empty;
    }
}
