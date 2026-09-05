using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ShortDrama.Infrastructure.Automation;

internal static class HongguoCdnDecryptor
{
    private sealed record Track(byte[] Handler, IReadOnlyList<int> Sizes, IReadOnlyList<long> Offsets);
    private readonly record struct Box(int ContentStart, int End);

    public static void Decrypt(string spadeA, string encryptedPath, string outputPath)
    {
        var keys = HongguoSpadeKey.UnwrapCandidates(spadeA);
        if (keys.Count == 0)
            throw new InvalidDataException("spade_a 解包未得到有效密钥");
        var data = File.ReadAllBytes(encryptedPath);
        var ivs = ReadSencIvs(data);
        if (ivs.Count == 0)
            throw new InvalidDataException("加密 MP4 未包含 senc IV");
        var tracks = ParseMediaTracks(data);
        var video = tracks.FirstOrDefault(track => track.Handler.AsSpan().SequenceEqual("vide"u8));
        if (video is null || video.Sizes.Count == 0)
            throw new InvalidDataException("加密 MP4 缺少视频轨道");

        var verified = VerifyKey(data, video, keys, ivs);
        if (verified is null)
            throw new InvalidDataException("spade_a 与加密 MP4 不匹配");

        var output = data.ToArray();
        var validVideoSamples = 0;
        var totalVideoSamples = 0;
        for (var trackIndex = 0; trackIndex < tracks.Count && trackIndex < ivs.Count; trackIndex++)
        {
            var track = tracks[trackIndex];
            var baseIv = ivs[trackIndex];
            for (var sampleIndex = 0; sampleIndex < track.Sizes.Count; sampleIndex++)
            {
                var offset = track.Offsets[sampleIndex];
                var size = track.Sizes[sampleIndex];
                ValidateSlice(output, offset, size);
                TransformCtr(output.AsSpan((int)offset, size), verified.Value.Key, baseIv + (ulong)sampleIndex);
                if (track.Handler.AsSpan().SequenceEqual("vide"u8))
                {
                    totalVideoSamples++;
                    if (IsValidNalSample(output.AsSpan((int)offset, size)))
                        validVideoSamples++;
                }
            }
        }
        if (totalVideoSamples == 0 || validVideoSamples != totalVideoSamples)
            throw new InvalidDataException($"解密后视频样本校验失败：有效 {validVideoSamples}/{totalVideoSamples}");
        File.WriteAllBytes(outputPath, output);
    }

    private static (byte[] Key, ulong Iv)? VerifyKey(
        byte[] data,
        Track video,
        IReadOnlyList<string> keys,
        IReadOnlyList<ulong> ivs)
    {
        var required = Math.Min(2, video.Sizes.Count);
        foreach (var keyText in keys)
        {
            var key = Convert.FromHexString(keyText);
            if (key.Length != 16)
                continue;
            foreach (var baseIv in ivs)
            {
                var ok = 0;
                for (var index = 0; index < required; index++)
                {
                    var offset = video.Offsets[index];
                    var size = video.Sizes[index];
                    ValidateSlice(data, offset, size);
                    var sample = data.AsSpan((int)offset, size).ToArray();
                    TransformCtr(sample, key, baseIv + (ulong)index);
                    if (!IsValidNalSample(sample))
                        break;
                    ok++;
                }
                if (ok >= required)
                    return (key, baseIv);
            }
        }
        return null;
    }

    private static void TransformCtr(Span<byte> data, byte[] key, ulong iv64)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();
        var counter = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(counter, iv64);
        var streamBlock = new byte[16];
        ulong low = 0;
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            BinaryPrimitives.WriteUInt64BigEndian(counter.AsSpan(8), low++);
            encryptor.TransformBlock(counter, 0, 16, streamBlock, 0);
            var count = Math.Min(16, data.Length - offset);
            for (var index = 0; index < count; index++)
                data[offset + index] ^= streamBlock[index];
        }
    }

    private static IReadOnlyList<ulong> ReadSencIvs(byte[] data)
    {
        var ivs = new List<ulong>();
        for (var index = 0; index + 20 <= data.Length; index++)
        {
            if (!data.AsSpan(index, 4).SequenceEqual("senc"u8))
                continue;
            var iv = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(index + 12, 8));
            if (!ivs.Contains(iv))
                ivs.Add(iv);
        }
        return ivs;
    }

    private static IReadOnlyList<Track> ParseMediaTracks(byte[] data)
    {
        var moov = FindBox(data, ["moov"u8.ToArray()]);
        if (moov is null)
            throw new InvalidDataException("MP4 缺少 moov box");
        var tracks = new List<Track>();
        foreach (var (type, box) in EnumerateBoxes(data, moov.Value.ContentStart, moov.Value.End))
        {
            if (!type.AsSpan().SequenceEqual("trak"u8))
                continue;
            var handlerBox = FindBox(data, ["mdia"u8.ToArray(), "hdlr"u8.ToArray()], box.ContentStart, box.End);
            if (handlerBox is null || handlerBox.Value.ContentStart + 12 > handlerBox.Value.End)
                continue;
            var handler = data.AsSpan(handlerBox.Value.ContentStart + 8, 4).ToArray();
            if (!handler.AsSpan().SequenceEqual("vide"u8) && !handler.AsSpan().SequenceEqual("soun"u8))
                continue;
            var (sizes, offsets) = ParseTrackSamples(data, box);
            if (sizes.Count > 0 && sizes.Count == offsets.Count)
                tracks.Add(new Track(handler, sizes, offsets));
        }
        if (tracks.Count == 0)
            throw new InvalidDataException("MP4 缺少媒体轨道");
        return tracks;
    }

    private static (IReadOnlyList<int> Sizes, IReadOnlyList<long> Offsets) ParseTrackSamples(byte[] data, Box track)
    {
        var stbl = FindBox(data, ["mdia"u8.ToArray(), "minf"u8.ToArray(), "stbl"u8.ToArray()], track.ContentStart, track.End)
                   ?? throw new InvalidDataException("MP4 缺少 stbl box");
        var stsz = FindBox(data, ["stsz"u8.ToArray()], stbl.ContentStart, stbl.End)
                   ?? throw new InvalidDataException("MP4 缺少 stsz box");
        var stsc = FindBox(data, ["stsc"u8.ToArray()], stbl.ContentStart, stbl.End)
                   ?? throw new InvalidDataException("MP4 缺少 stsc box");
        var stco = FindBox(data, ["stco"u8.ToArray()], stbl.ContentStart, stbl.End);
        var co64 = FindBox(data, ["co64"u8.ToArray()], stbl.ContentStart, stbl.End);
        if (stco is null && co64 is null)
            throw new InvalidDataException("MP4 缺少 chunk offset box");

        var sampleSize = ReadU32(data, stsz.ContentStart + 4);
        var sampleCount = checked((int)ReadU32(data, stsz.ContentStart + 8));
        var sizes = new List<int>(sampleCount);
        for (var index = 0; index < sampleCount; index++)
            sizes.Add(sampleSize > 0 ? checked((int)sampleSize) : checked((int)ReadU32(data, stsz.ContentStart + 12 + 4 * index)));

        var chunks = new List<long>();
        var chunkBox = stco ?? co64!.Value;
        var chunkCount = checked((int)ReadU32(data, chunkBox.ContentStart + 4));
        for (var index = 0; index < chunkCount; index++)
            chunks.Add(stco is not null
                ? ReadU32(data, chunkBox.ContentStart + 8 + 4 * index)
                : checked((long)ReadU64(data, chunkBox.ContentStart + 8 + 8 * index)));

        var entryCount = checked((int)ReadU32(data, stsc.ContentStart + 4));
        var runs = new List<(int FirstChunk, int Samples)>(entryCount);
        for (var index = 0; index < entryCount; index++)
            runs.Add((checked((int)ReadU32(data, stsc.ContentStart + 8 + 12 * index)),
                checked((int)ReadU32(data, stsc.ContentStart + 12 + 12 * index))));
        var samplesPerChunk = new int[chunks.Count];
        for (var index = 0; index < runs.Count; index++)
        {
            var last = index + 1 < runs.Count ? runs[index + 1].FirstChunk - 1 : chunks.Count;
            for (var chunk = runs[index].FirstChunk; chunk <= last && chunk <= chunks.Count; chunk++)
                if (chunk >= 1)
                    samplesPerChunk[chunk - 1] = runs[index].Samples;
        }

        var offsets = new List<long>(sampleCount);
        var sampleIndex = 0;
        for (var chunkIndex = 0; chunkIndex < chunks.Count && sampleIndex < sampleCount; chunkIndex++)
        {
            var offset = chunks[chunkIndex];
            for (var sample = 0; sample < samplesPerChunk[chunkIndex] && sampleIndex < sampleCount; sample++)
            {
                offsets.Add(offset);
                offset += sizes[sampleIndex++];
            }
        }
        return (sizes, offsets);
    }

    private static Box? FindBox(byte[] data, IReadOnlyList<byte[]> path, int start = 0, int? end = null)
    {
        foreach (var (type, box) in EnumerateBoxes(data, start, end ?? data.Length))
        {
            if (!type.AsSpan().SequenceEqual(path[0]))
                continue;
            return path.Count == 1 ? box : FindBox(data, path.Skip(1).ToArray(), box.ContentStart, box.End);
        }
        return null;
    }

    private static IEnumerable<(byte[] Type, Box Box)> EnumerateBoxes(byte[] data, int start, int end)
    {
        var offset = start;
        while (offset + 8 <= end)
        {
            var size = (long)ReadU32(data, offset);
            var type = data.AsSpan(offset + 4, 4).ToArray();
            var header = 8;
            if (size == 1)
            {
                if (offset + 16 > end)
                    yield break;
                size = checked((long)ReadU64(data, offset + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - offset;
            }
            if (size < header || offset + size > end)
                yield break;
            yield return (type, new Box(offset + header, checked((int)(offset + size))));
            offset = checked((int)(offset + size));
        }
    }

    private static bool IsValidNalSample(ReadOnlySpan<byte> sample)
    {
        var offset = 0;
        while (offset + 4 <= sample.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(sample.Slice(offset, 4)));
            if (length <= 0 || offset + 4L + length > sample.Length)
                return false;
            offset += 4 + length;
        }
        return offset == sample.Length;
    }

    private static void ValidateSlice(byte[] data, long offset, int size)
    {
        if (offset < 0 || size < 0 || offset > int.MaxValue || offset + size > data.LongLength)
            throw new InvalidDataException("MP4 样本偏移超出文件范围");
    }

    private static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static ulong ReadU64(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
}
