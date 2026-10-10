using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Automation;

internal static class DramaPlayCount
{
    private static readonly string[] Keys =
    [
        "read_count",
        "play_count",
        "play_cnt",
        "play_num",
        "read_cnt",
        "view_count"
    ];

    public static int Read(JsonElement element, Func<JsonElement, string, int?> read)
    {
        foreach (var key in Keys)
        {
            var value = read(element, key);
            if (value is > 0)
            {
                return value.Value;
            }
        }

        return 0;
    }

    public static int Read(JsonObject? element, Func<JsonObject?, string, int?> read)
    {
        if (element is null)
        {
            return 0;
        }

        foreach (var key in Keys)
        {
            var value = read(element, key);
            if (value is > 0)
            {
                return value.Value;
            }
        }

        return 0;
    }
}
