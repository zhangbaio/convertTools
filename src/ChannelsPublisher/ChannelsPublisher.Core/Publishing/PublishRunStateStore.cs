using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ChannelsPublisher.Core.Services;

namespace ChannelsPublisher.Core.Publishing;

/// <summary>断点续传状态：记录已成功发布的素材签名，供 resume 策略跳过（停止/重启后仍复用）。
/// 持久化到 %LocalAppData%/ChannelsPublisher/publish-run-state.json。</summary>
public sealed class PublishRunStateStore
{
    public sealed class DoneEntry
    {
        public string At { get; set; } = "";
        public string Video { get; set; } = "";
        public string Account { get; set; } = "";
    }

    private Dictionary<string, DoneEntry> _done = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(AppPaths.DataRoot, "publish-run-state.json");

    public static PublishRunStateStore Load()
    {
        var store = new PublishRunStateStore();
        try
        {
            if (File.Exists(FilePath))
                store._done = JsonSerializer.Deserialize<Dictionary<string, DoneEntry>>(File.ReadAllText(FilePath), Options)
                              ?? new Dictionary<string, DoneEntry>();
        }
        catch { /* 损坏则按空处理 */ }
        return store;
    }

    public int Count => _done.Count;

    public bool IsDone(string signature) => _done.ContainsKey(signature);

    public void MarkDone(string signature, string video, string account)
    {
        _done[signature] = new DoneEntry { At = DateTime.UtcNow.ToString("o"), Video = video, Account = account };
        Save();
    }

    public void Reset()
    {
        _done.Clear();
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_done, Options));
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
