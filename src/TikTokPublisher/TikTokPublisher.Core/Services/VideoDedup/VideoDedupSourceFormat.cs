using System.Globalization;
using System.Text.Json;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal sealed record VideoDedupSourceFormat(
    string Codec, string Profile, string PixelFormat, string SampleAspectRatio,
    string Range, string ColorSpace, string Transfer, string Primaries,
    string FieldOrder, string NominalFrameRate, bool HasDisplayTransform, int VideoStreams, int AudioStreams)
{
    public static VideoDedupSourceFormat Read(JsonElement stream, int videoStreams, int audioStreams)
    {
        string Value(string key) =>
            stream.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
                ? field.GetString() ?? "" : "";

        var hasDisplayTransform = stream.TryGetProperty("side_data_list", out var sideData) &&
            sideData.ValueKind == JsonValueKind.Array &&
            sideData.EnumerateArray().Any(item => item.TryGetProperty("displaymatrix", out _) ||
                item.TryGetProperty("rotation", out _));
        return new(Value("codec_name"), Value("profile"), Value("pix_fmt"), Value("sample_aspect_ratio"),
            Value("color_range"), Value("color_space"), Value("color_transfer"), Value("color_primaries"),
            Value("field_order"), Value("r_frame_rate"), hasDisplayTransform, videoStreams, audioStreams);
    }

    public bool IsSupported =>
        Codec is "h264" or "hevc" && PixelFormat == "yuv420p" && FieldOrder is "progressive" or "" or "unknown" &&
        Range is "tv" or "" && IsBt709(ColorSpace) && IsBt709(Transfer) && IsBt709(Primaries) &&
        !HasDisplayTransform && VideoStreams == 1 && AudioStreams <= 1;

    private static bool IsBt709(string value) => value is "bt709" or "";

    public static bool TryReadRatio(string ratio, char separator, out int numerator, out int denominator)
    {
        numerator = denominator = 0;
        var parts = ratio.Split(separator, 2);
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out numerator)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out denominator)
            && numerator > 0 && denominator > 0;
    }
}
