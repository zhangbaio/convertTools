using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ShortDrama.Infrastructure.Automation;

internal static class DramaBoxPrivateMp4Decryptor
{
    private static readonly byte[] Marker = "AliyunPrivateKeyUri"u8.ToArray();

    public static bool ContainsPrivateKey(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var window = new byte[Math.Min(256 * 1024, stream.Length)];
            var read = stream.Read(window);
            return window.AsSpan(0, read).IndexOf(Marker) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public static string? ReadPrivateKeyUri(ReadOnlySpan<byte> data)
    {
        var index = data.IndexOf(Marker);
        if (index < 0)
            return null;
        var cursor = index + Marker.Length;
        var limit = Math.Min(data.Length, cursor + 4096);
        while (cursor < limit)
        {
            while (cursor < limit && !IsBase64(data[cursor]))
                cursor++;
            var start = cursor;
            while (cursor < limit && IsBase64(data[cursor]))
                cursor++;
            if (cursor - start >= 80)
                return Encoding.ASCII.GetString(data[start..cursor]);
        }

        return null;
    }

    public static bool TryDecrypt(byte[] file, KeyPackage package, string appId, string deviceId)
    {
        if (!TryUnwrap(DeriveWrappingKey(package, appId, deviceId), package, out var contentKey))
            return false;
        return DecryptSamples(file, contentKey);
    }

    internal static string BuildWrappingMaterial(KeyPackage package, string appId, string deviceId)
    {
        var episode = Math.Max(1, package.EpisodeNumber);
        return string.Concat(
            "dr-private-mp4-key-v2\n",
            appId, "\n",
            deviceId, "\n",
            episode.ToString(CultureInfo.InvariantCulture), "\n",
            package.VideoId ?? "", "\n",
            package.PrivateKeyUriHash, "\n",
            package.NonceBase64, "\n",
            package.ExpiresAt.ToString(CultureInfo.InvariantCulture), "\n",
            package.Entitlement ?? "");
    }

    private static byte[] DeriveWrappingKey(KeyPackage package, string appId, string deviceId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(BuildWrappingMaterial(package, appId, deviceId)));

    private static bool TryUnwrap(byte[] wrappingKey, KeyPackage package, out byte[] contentKey)
    {
        contentKey = [];
        try
        {
            contentKey = new byte[package.Ciphertext.Length];
            using var gcm = new AesGcm(wrappingKey, package.Tag.Length);
            gcm.Decrypt(package.Nonce, package.Ciphertext, package.Tag, contentKey);
            return contentKey.Length is 16 or 24 or 32;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool DecryptSamples(byte[] file, byte[] contentKey)
    {
        var snapshot = (byte[])file.Clone();
        var tracks = ParseTracks(snapshot);
        var video = tracks.FirstOrDefault(track => track.Handler is not null && track.Handler.AsSpan().SequenceEqual("vide"u8));
        if (video.Sizes is null || video.Sizes.Count == 0)
            return false;
        Apply(snapshot, tracks, contentKey);
        if (!IsValidAvcc(snapshot, video.Offsets[0], video.Sizes[0]))
            return false;
        Apply(file, tracks, contentKey);
        return true;
    }

    private static void Apply(byte[] file, IReadOnlyList<Track> tracks, byte[] contentKey)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = contentKey.Length == 16 ? contentKey : contentKey[..16];
        using var transform = aes.CreateDecryptor();
        var block = new byte[16];
        foreach (var track in tracks)
        {
            for (var index = 0; index < track.Sizes.Count; index++)
            {
                var size = track.Sizes[index] & ~15;
                var offset = checked((int)track.Offsets[index]);
                for (var cursor = 0; cursor < size; cursor += 16)
                {
                    transform.TransformBlock(file, offset + cursor, 16, block, 0);
                    block.CopyTo(file.AsSpan(offset + cursor, 16));
                }
            }
        }
    }

    private static bool IsValidAvcc(byte[] file, long offset, int size)
    {
        var cursor = 0;
        while (cursor + 4 <= size)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(checked((int)offset + cursor), 4));
            if (length <= 0 || cursor + 4L + length > size)
                return false;
            cursor += 4 + length;
        }

        return cursor == size;
    }

    private static List<Track> ParseTracks(byte[] data)
    {
        var moov = FindChild(data, 0, data.Length, "moov"u8);
        if (moov is null)
            return [];
        var tracks = new List<Track>();
        foreach (var trak in Children(data, moov.Value.Start, moov.Value.End).Where(box => box.Type.AsSpan().SequenceEqual("trak"u8)))
        {
            var mdia = FindChild(data, trak.Start, trak.End, "mdia"u8);
            if (mdia is null)
                continue;
            var hdlr = FindChild(data, mdia.Value.Start, mdia.Value.End, "hdlr"u8);
            if (hdlr is null || hdlr.Value.Start + 12 > hdlr.Value.End)
                continue;
            var handler = data.AsSpan(hdlr.Value.Start + 8, 4).ToArray();
            if (!handler.AsSpan().SequenceEqual("vide"u8) && !handler.AsSpan().SequenceEqual("soun"u8))
                continue;
            var minf = FindChild(data, mdia.Value.Start, mdia.Value.End, "minf"u8);
            var stbl = minf is null ? null : FindChild(data, minf.Value.Start, minf.Value.End, "stbl"u8);
            if (stbl is null)
                continue;
            var samples = ParseSamples(data, stbl.Value.Start, stbl.Value.End);
            if (samples.Sizes.Count > 0)
                tracks.Add(new Track(handler, samples.Sizes, samples.Offsets));
        }

        return tracks;
    }

    private static (List<int> Sizes, List<long> Offsets) ParseSamples(byte[] data, int start, int end)
    {
        var stsz = FindChild(data, start, end, "stsz"u8);
        var stsc = FindChild(data, start, end, "stsc"u8);
        var stco = FindChild(data, start, end, "stco"u8);
        var co64 = FindChild(data, start, end, "co64"u8);
        if (stsz is null || stsc is null || (stco is null && co64 is null))
            return ([], []);
        var defaultSize = ReadU32(data, stsz.Value.Start + 4);
        var count = (int)ReadU32(data, stsz.Value.Start + 8);
        var sizes = new List<int>(count);
        for (var index = 0; index < count; index++)
            sizes.Add(defaultSize > 0 ? (int)defaultSize : (int)ReadU32(data, stsz.Value.Start + 12 + 4 * index));
        var chunkBox = stco ?? co64!.Value;
        var chunkCount = (int)ReadU32(data, chunkBox.Start + 4);
        var chunks = new List<long>(chunkCount);
        for (var index = 0; index < chunkCount; index++)
            chunks.Add(stco is not null
                ? ReadU32(data, chunkBox.Start + 8 + 4 * index)
                : (long)ReadU64(data, chunkBox.Start + 8 + 8 * index));
        var runs = (int)ReadU32(data, stsc.Value.Start + 4);
        var samplesPerChunk = new int[chunks.Count];
        for (var index = 0; index < runs; index++)
        {
            var first = (int)ReadU32(data, stsc.Value.Start + 8 + 12 * index);
            var perChunk = (int)ReadU32(data, stsc.Value.Start + 12 + 12 * index);
            var last = index + 1 < runs ? (int)ReadU32(data, stsc.Value.Start + 8 + 12 * (index + 1)) - 1 : chunks.Count;
            for (var chunk = first; chunk <= last && chunk <= chunks.Count; chunk++)
                if (chunk >= 1)
                    samplesPerChunk[chunk - 1] = perChunk;
        }

        var offsets = new List<long>(count);
        var sample = 0;
        for (var chunk = 0; chunk < chunks.Count && sample < count; chunk++)
        {
            var offset = chunks[chunk];
            for (var inner = 0; inner < samplesPerChunk[chunk] && sample < count; inner++)
            {
                offsets.Add(offset);
                offset += sizes[sample++];
            }
        }

        return (sizes, offsets);
    }

    private static IEnumerable<Box> Children(byte[] data, int start, int end)
    {
        var offset = start;
        while (offset + 8 <= end)
        {
            var size = (long)ReadU32(data, offset);
            var header = 8;
            if (size == 1)
            {
                size = (long)ReadU64(data, offset + 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - offset;
            }

            if (size < header || offset + size > end)
                yield break;
            yield return new Box(data.AsSpan(offset + 4, 4).ToArray(), offset + header, (int)(offset + size));
            offset += (int)size;
        }
    }

    private static Box? FindChild(byte[] data, int start, int end, ReadOnlySpan<byte> type)
    {
        foreach (var box in Children(data, start, end))
        {
            if (box.Type.AsSpan().SequenceEqual(type))
                return box;
        }

        return null;
    }

    private static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static ulong ReadU64(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));

    private static bool IsBase64(byte value) =>
        value is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'+' or (byte)'/' or (byte)'=';

    internal readonly record struct KeyPackage(
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag,
        string NonceBase64,
        string PrivateKeyUri,
        string PrivateKeyUriHash,
        string? Entitlement,
        int Version,
        int EpisodeNumber,
        long ExpiresAt,
        string VideoId);

    private readonly record struct Box(byte[] Type, int Start, int End);
    private readonly record struct Track(byte[] Handler, List<int> Sizes, List<long> Offsets);
}
