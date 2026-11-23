using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Drama;

/// <summary>Records the account and provider that identified a title during manual TikTok import.</summary>
public sealed record TikTokUploadTitleRoute(
    string AccountProfileId,
    string DramaMarket,
    string DramaSourceChain,
    string BookId = "")
{
    private const string MarkerFileName = ".tiktok-upload-title-route.json";

    public static TikTokUploadTitleRoute Capture(ClientSettings settings, TikTokAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.Id))
            throw new InvalidOperationException("请先选择并保存用于上传短剧的 TikTok 账号。");

        var market = TikTokAccountProfile.NormalizeDramaMarket(account.TiktokDramaMarket);
        var configured = (market == TikTokAccountProfile.DramaMarketOverseas
            ? settings.OverseasDramaSourceChain : settings.DramaSourceChain)?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(configured) && !IsKnownSource(configured))
            throw new InvalidOperationException($"{MarketLabel(market)}数据链路配置无效，请到系统设置重新选择。");

        var route = new TikTokUploadTitleRoute(account.Id, market, VisibleDramaSource.ChainFor(settings, market));
        route.ApplyTo(settings);
        return route;
    }

    public ClientSettings ApplyTo(ClientSettings settings)
    {
        var scopedSettings = settings.Clone();
        // Use the captured provider. Other account settings cannot change it mid-import.
        scopedSettings.DramaSourceChain = DramaSourceChain;
        var configuration = DramaSourceConfigurationValidator.Check(scopedSettings);
        if (!configuration.IsConfigured)
            throw new InvalidOperationException(configuration.Message);

        return scopedSettings;
    }

    public string Describe() => $"{MarketLabel(DramaMarket)} · {VisibleDramaSource.Label(DramaSourceChain)}";

    public static TikTokUploadTitleRoute? Read(string sourceProjectDir)
    {
        var path = Path.Combine(sourceProjectDir, MarkerFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var route = JsonSerializer.Deserialize<TikTokUploadTitleRoute>(File.ReadAllText(path));
            if (route is null || string.IsNullOrWhiteSpace(route.AccountProfileId) ||
                route.DramaMarket is not (TikTokAccountProfile.DramaMarketLocal or TikTokAccountProfile.DramaMarketOverseas) ||
                !IsKnownSource(route.DramaSourceChain) || !QueueMaterialStepService.IsBookIdForSource(route.BookId, route.DramaSourceChain))
            {
                throw new InvalidDataException("项目的上传短剧链路记录无效，请在新的工作目录重新导入该剧。");
            }

            return route;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("项目的上传短剧链路记录无法读取，请在新的工作目录重新导入该剧。", ex);
        }
    }

    internal static string? FindProjectWithRoute(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            return null;

        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDir));
        if (File.Exists(Path.Combine(resolved, MarkerFileName)))
            return resolved;

        if (!string.Equals(Directory.GetParent(resolved)?.Name, "workflow", StringComparison.OrdinalIgnoreCase))
            return null;

        var source = ProjectWorkspaceService.LoadContext(resolved).SourceProjectDir;
        return File.Exists(Path.Combine(source, MarkerFileName)) ? source : null;
    }

    internal void Save(string projectDir, string bookId)
    {
        if (!QueueMaterialStepService.IsBookIdForSource(bookId, DramaSourceChain))
            throw new InvalidDataException($"{VisibleDramaSource.Label(DramaSourceChain)} 返回了不属于该链路的剧集标识。");

        var path = Path.Combine(projectDir, MarkerFileName);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this with { BookId = bookId.Trim() }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            ResilientFileSystem.TryDeleteFile(temporary);
        }
    }

    internal static void EnsureAccountOwnsProject(string projectDir, string? accountProfileId)
    {
        var existing = Read(projectDir);
        if (existing is not null && !string.Equals(existing.AccountProfileId, accountProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("该项目已通过另一个 TikTok 账号导入，请在当前账号的独立工作目录中导入。");
    }

    private static bool IsKnownSource(string? value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(VisibleDramaSource.Normalize(value), value, StringComparison.Ordinal);

    private static string MarketLabel(string market) =>
        market == TikTokAccountProfile.DramaMarketOverseas ? "海外剧" : "本土剧";
}
