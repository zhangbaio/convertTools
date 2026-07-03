using System.Buffers.Binary;
using System.Text;
using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Services;

public static class TikTokSmallVideoPaddingService
{
    private const double ProbeDurationToleranceSeconds = 0.05;
    private const double ProbeFrameRateToleranceFps = 0.1;

    public static bool NeedsPadding(string path)
    {
        try { return new FileInfo(path).Length < TikTokVideoConstraints.MinSizeBytes; }
        catch { return false; }
    }

    public static bool SupportsPadding(string path) =>
        TikTokVideoConstraints.PaddingSupportedExtensions.Contains(Path.GetExtension(path));

    public static async Task<bool> PadWithoutReencodeAsync(
        string videoPath,
        Action<string>? log,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(videoPath);
        long originalSize;
        try { originalSize = new FileInfo(fullPath).Length; }
        catch (Exception ex) { throw new InvalidOperationException($"读取视频大小失败：{Path.GetFileName(fullPath)}（{ex.Message}）"); }

        if (originalSize >= TikTokVideoConstraints.MinSizeBytes) return false;
        if (!SupportsPadding(fullPath)) return false;

        var ffprobe = MediaBinaryResolver.ResolveFfprobe();
        var originalProbe = await MediaProbe.ProbeAsync(ffprobe, fullPath, ct);
        var targetSize = Math.Max(TikTokVideoConstraints.PaddingTargetBytes, TikTokVideoConstraints.MinSizeBytes);
        var paddingSize = Math.Max(8, (int)(targetSize - originalSize));

        await using (var handle = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            WriteMp4FreeBox(handle, paddingSize);
        }

        try
        {
            var repairedProbe = await MediaProbe.ProbeAsync(ffprobe, fullPath, ct);
            AssertProbeStable(originalProbe, repairedProbe, fullPath);
        }
        catch
        {
            await using var handle = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            handle.SetLength(originalSize);
            throw;
        }

        var repairedSize = new FileInfo(fullPath).Length;
        log?.Invoke(
            $"小文件自动修复：{Path.GetFileName(fullPath)} | {FormatSize(originalSize)} -> {FormatSize(repairedSize)} | 仅追加 MP4 free box");
        return true;
    }

    public static void CopyForPadding(string sourcePath, string targetPath)
    {
        var target = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.Delete(target);
        File.Copy(Path.GetFullPath(sourcePath), target, overwrite: false);
    }

    private static void WriteMp4FreeBox(Stream handle, long boxSize)
    {
        var normalized = Math.Max(8L, boxSize);
        if (normalized >= 1L << 32)
            throw new InvalidOperationException($"补齐数据过大：{normalized} bytes");
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)normalized);
        Encoding.ASCII.GetBytes("free", header[4..]);
        handle.Write(header);
        var payload = (int)(normalized - 8);
        if (payload > 0)
            handle.Write(new byte[payload]);
    }

    private static void AssertProbeStable(MediaProbe original, MediaProbe repaired, string videoPath)
    {
        if (Math.Abs(repaired.DurationSeconds - original.DurationSeconds) > ProbeDurationToleranceSeconds)
            throw new InvalidOperationException($"自动修复后时长发生变化：{Path.GetFileName(videoPath)}");
        if (repaired.Width != original.Width || repaired.Height != original.Height)
            throw new InvalidOperationException($"自动修复后分辨率发生变化：{Path.GetFileName(videoPath)}");
        if (Math.Abs(repaired.FrameRateFps - original.FrameRateFps) > ProbeFrameRateToleranceFps)
            throw new InvalidOperationException($"自动修复后帧率发生变化：{Path.GetFileName(videoPath)}");
        if (!string.Equals(repaired.AudioCodec, original.AudioCodec, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"自动修复后音轨格式发生变化：{Path.GetFileName(videoPath)}");
    }

    public static string FormatSize(long value)
    {
        var size = (double)Math.Max(0, value);
        string[] units = ["B", "KB", "MB", "GB"];
        var idx = 0;
        while (size >= 1024 && idx < units.Length - 1) { size /= 1024; idx++; }
        return $"{size:F1} {units[idx]}";
    }
}
