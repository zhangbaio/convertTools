using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PlatformPublisher.Weixin.Publishing;

internal enum WeixinSeriesSubmissionState
{
    None,
    Pending,
    Submitted,
}

internal sealed record WeixinSeriesApiState
{
    public WeixinSeriesSubmissionState Submission { get; init; }
    public string? Title { get; init; }
    public List<WeixinSeriesApiReceipt> Files { get; init; } = [];
}

internal sealed class WeixinSeriesApiStateStore(string root)
{
    public AccountScope OpenAccount(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        Directory.CreateDirectory(root);
        return new AccountScope(root, Hash(accountId.ToUpperInvariant()));
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static FileStream LockFile(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("该视频号账号或项目正在执行另一项操作，请稍后重试。", ex);
        }
    }

    private static void Save<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream) ?? throw new JsonException();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("视频号接口上传记录无法读取。请先核对平台记录和本地回执，再继续操作。", ex);
        }
    }

    internal sealed class AccountScope : IDisposable
    {
        private readonly string _root;
        private readonly string _identityPath;
        private readonly FileStream _lease;

        public AccountScope(string dataRoot, string accountKey)
        {
            _root = dataRoot;
            _identityPath = Path.Combine(dataRoot, accountKey + ".account.json");
            _lease = LockFile(_identityPath + ".lock");
        }

        public void VerifyIdentity(string finderId)
        {
            var stored = Read<string>(_identityPath);
            if (stored is not null && !string.Equals(stored, Hash(finderId), StringComparison.Ordinal))
                throw new InvalidOperationException("当前网页登录的视频号与此账号已绑定的视频号不一致，请切回原视频号或使用另一个账号档案。");
        }

        public void BindIdentity(string finderId)
        {
            VerifyIdentity(finderId);
            if (!File.Exists(_identityPath))
                Save(_identityPath, Hash(finderId));
        }

        public ProjectScope? OpenProject(string projectDirectory, bool create = false)
        {
            var identity = Read<string>(_identityPath);
            if (identity is null)
                return null;
            var marker = Path.Combine(Path.GetFullPath(projectDirectory), ".weixin-series-project-id");
            var projectId = Read<string>(marker);
            if (projectId is null && !create)
                return null;
            if (projectId is null)
            {
                var candidate = Guid.NewGuid().ToString("N");
                var temp = marker + "." + candidate + ".tmp";
                try
                {
                    Save(temp, candidate);
                    try { File.Move(temp, marker); }
                    catch (IOException) when (File.Exists(marker)) { }
                }
                finally
                {
                    File.Delete(temp);
                }
                projectId = Read<string>(marker);
            }
            if (!Guid.TryParseExact(projectId, "N", out var stableId))
                throw new InvalidOperationException("视频号项目标识无法读取，请先核对该项目的提审记录。");
            var key = Hash(identity + "\n" + stableId.ToString("N"));
            return new ProjectScope(Path.Combine(_root, key + ".project.json"));
        }

        public void Dispose() => _lease.Dispose();
    }

    internal sealed class ProjectScope(string path) : IDisposable
    {
        private readonly FileStream _lease = LockFile(path + ".lock");

        public WeixinSeriesApiState Load()
        {
            var state = Read<WeixinSeriesApiState>(path) ?? new WeixinSeriesApiState();
            if (!Enum.IsDefined(state.Submission) || state.Files is null ||
                state.Files.Any(item => item is null || item.File is null || string.IsNullOrWhiteSpace(item.MediaId)) ||
                (state.Submission != WeixinSeriesSubmissionState.None && string.IsNullOrWhiteSpace(state.Title)))
                throw new InvalidOperationException("视频号接口上传记录格式无效，请核对平台状态。");
            return state;
        }

        public void Store(WeixinSeriesApiState state) => Save(path, state);
        public void Dispose() => _lease.Dispose();
    }
}
