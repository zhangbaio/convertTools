using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChannelsPublisher.Core.Services;

namespace ChannelsPublisher.Core.Publishing;

/// <summary>记录视频号素材的提交结果和待核对的提交，供后续运行查询。</summary>
public sealed class PublishRunStateStore
{
    internal enum Status { Missing, Pending, Completed }

    internal sealed class Entry
    {
        public string At { get; set; } = "";
        public string Video { get; set; } = "";
        public string Account { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Pending { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _filePath;

    public PublishRunStateStore(string? filePath = null) =>
        _filePath = Path.GetFullPath(filePath ?? Path.Combine(AppPaths.DataRoot, "publish-run-state.json"));

    internal Run OpenRun()
    {
        var lease = AcquireLease();
        try
        {
            return new Run(this, lease, Read());
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Reset()
    {
        using var lease = AcquireLease();
        Save(new Dictionary<string, Entry>());
    }

    public bool ResolvePending(string accountId, PublishItem item, bool submitted)
    {
        using var run = OpenRun();
        return run.ResolvePending(SignatureFor(accountId, item), submitted);
    }

    internal static string ScopedSignature(string signature, FinalAction action) => $"{signature}|{action}";

    private FileStream AcquireLease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        try
        {
            return new FileStream(_filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException($"无法锁定视频号续传记录。请检查目录权限，或等待其他客户端结束发布：{_filePath}", ex);
        }
    }

    private Dictionary<string, Entry> Read()
    {
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_filePath), Options);
            if (entries is null || entries.Values.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.At)))
                throw new JsonException();
            return entries;
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<string, Entry>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"视频号续传记录损坏。请先到平台核对已发表素材和草稿，再恢复或清除记录：{_filePath}", ex);
        }
    }

    private void Save(Dictionary<string, Entry> entries)
    {
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entries, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法保存视频号续传记录：{_filePath}", ex);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class Run(PublishRunStateStore store, FileStream lease, Dictionary<string, Entry> entries) : IDisposable
    {
        private readonly object _gate = new();
        private Dictionary<string, Entry> _entries = entries;

        public Status GetStatus(string signature)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(signature, out var entry))
                    return Status.Missing;
                return entry.Pending ? Status.Pending : Status.Completed;
            }
        }

        public Status GetStatus(string signature, FinalAction action)
        {
            var legacy = GetStatus(signature);
            var draft = GetStatus(ScopedSignature(signature, FinalAction.Draft));
            var publish = GetStatus(ScopedSignature(signature, FinalAction.Publish));
            if (legacy == Status.Pending || draft == Status.Pending || publish == Status.Pending)
                return Status.Pending;
            if (legacy == Status.Completed || publish == Status.Completed)
                return Status.Completed;
            return action == FinalAction.Draft ? draft : Status.Missing;
        }

        public bool ResolvePending(string signature, bool submitted)
        {
            lock (_gate)
            {
                var updated = new Dictionary<string, Entry>(_entries);
                var changed = false;
                foreach (var key in new[] { signature, ScopedSignature(signature, FinalAction.Draft),
                             ScopedSignature(signature, FinalAction.Publish) })
                {
                    if (!updated.TryGetValue(key, out var entry) || !entry.Pending)
                        continue;
                    changed = true;
                    if (submitted)
                        updated[key] = new Entry { At = entry.At, Video = entry.Video, Account = entry.Account };
                    else
                        updated.Remove(key);
                }
                if (!changed)
                    return false;
                store.Save(updated);
                _entries = updated;
                return true;
            }
        }

        public void Record(string signature, string video, string account, bool pending)
        {
            lock (_gate)
            {
                var updated = new Dictionary<string, Entry>(_entries)
                {
                    [signature] = new Entry { At = DateTime.UtcNow.ToString("o"), Video = video, Account = account, Pending = pending }
                };
                store.Save(updated);
                _entries = updated;
            }
        }

        public void Dispose() => lease.Dispose();
    }

    /// <summary>素材签名：账号 + 视频路径（小写）+ 文件大小。同账号同一文件视为同一次发布。</summary>
    public static string SignatureFor(string accountId, PublishItem item)
    {
        long size = 0;
        try { if (File.Exists(item.VideoPath)) size = new FileInfo(item.VideoPath).Length; }
        catch { /* 取不到大小则记 0 */ }
        var path = (item.VideoPath ?? "").Trim().ToLowerInvariant();
        return $"{accountId}|{path}|{size}";
    }
}
