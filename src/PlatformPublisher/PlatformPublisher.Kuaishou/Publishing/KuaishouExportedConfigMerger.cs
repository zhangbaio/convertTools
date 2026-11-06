using System.Reflection;
using System.Text.Json;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouExportedConfigMerger
{
    public static void Apply(
        JsonElement settings,
        JsonElement profile,
        KuaishouPersonalConfig target,
        PublishPlatform platform)
    {
        using var merged = Merge(settings, profile);
        var before = Copy(target);
        KuaishouLegacyConfigImporter.ImportElement(merged.RootElement, target, platform, "weixin-channel-tool-export");
        RestoreNonEmptyStrings(before, target);
    }

    public static void ShareSecrets(KuaishouPersonalConfig left, KuaishouPersonalConfig right)
    {
        CopySecretIfEmpty(left, right, config => config.AppSecret, (config, value) => config.AppSecret = value);
        CopySecretIfEmpty(left, right, config => config.AuthCode, (config, value) => config.AuthCode = value);
        CopySecretIfEmpty(left, right, config => config.AccessToken, (config, value) => config.AccessToken = value);
        CopySecretIfEmpty(left, right, config => config.RefreshToken, (config, value) => config.RefreshToken = value);
        CopySecretIfEmpty(left, right, config => config.AccessTokenExpiresAt, (config, value) => config.AccessTokenExpiresAt = value);
        CopySecretIfEmpty(left, right, config => config.RefreshTokenExpiresAt, (config, value) => config.RefreshTokenExpiresAt = value);
    }

    private static JsonDocument Merge(JsonElement settings, JsonElement profile)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (settings.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in settings.EnumerateObject())
            {
                if (property.NameEquals("account_profiles_json") || property.NameEquals("active_account_profile_id"))
                    continue;
                values[property.Name] = property.Value.Clone();
            }
        }
        if (profile.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in profile.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in values)
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    private static KuaishouPersonalConfig Copy(KuaishouPersonalConfig source)
    {
        var copy = new KuaishouPersonalConfig();
        foreach (var property in typeof(KuaishouPersonalConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                continue;
            property.SetValue(copy, property.GetValue(source));
        }
        return copy;
    }

    private static void RestoreNonEmptyStrings(KuaishouPersonalConfig before, KuaishouPersonalConfig after)
    {
        foreach (var property in typeof(KuaishouPersonalConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.PropertyType != typeof(string) || !property.CanRead || !property.CanWrite)
                continue;
            var previous = property.GetValue(before) as string;
            var current = property.GetValue(after) as string;
            if (!string.IsNullOrWhiteSpace(previous) && string.IsNullOrWhiteSpace(current))
                property.SetValue(after, previous);
        }
    }

    private static void CopySecretIfEmpty(
        KuaishouPersonalConfig left,
        KuaishouPersonalConfig right,
        Func<KuaishouPersonalConfig, string> read,
        Action<KuaishouPersonalConfig, string> write)
    {
        var leftValue = read(left);
        var rightValue = read(right);
        if (string.IsNullOrWhiteSpace(leftValue) && !string.IsNullOrWhiteSpace(rightValue))
            write(left, rightValue);
        if (string.IsNullOrWhiteSpace(rightValue) && !string.IsNullOrWhiteSpace(leftValue))
            write(right, leftValue);
    }
}
