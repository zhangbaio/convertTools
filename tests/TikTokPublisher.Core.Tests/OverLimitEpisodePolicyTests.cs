using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class OverLimitEpisodePolicyTests
{
    [Fact]
    public void Account_limits_are_independent_and_legacy_accounts_use_30_to_300()
    {
        var legacy = JsonSerializer.Deserialize<TikTokAccountProfile>("{}")!;
        var configured = new TikTokAccountProfile { TiktokMinimumEpisodeCount = 42, TiktokMaximumEpisodeCount = 165 };
        var saved = JsonSerializer.Deserialize<TikTokAccountProfile>(JsonSerializer.Serialize(configured))!;
        var global = new ClientSettings();

        OverLimitEpisodePolicy.ResolveEpisodeBounds(legacy).Should().Be((30, 300));
        var options = TikTokMaterialValidationService.Options.FromAccount(saved, global);
        (options.MinimumEpisodeCount, options.MaximumEpisodeCount).Should().Be((42, 165));
        OverLimitEpisodePolicy.ResolveKeepCount(OverLimitEpisodePolicy.WithAccountBounds(global, saved)).Should().Be(165);
        OverLimitEpisodePolicy.ResolveKeepCount(global).Should().Be(300);
    }

    private static ClientSettings Settings(bool enabled = true, int keep = 120) => new()
    {
        TiktokAllowOverLimitUploadImport = enabled,
        TiktokMaximumEpisodeCount = keep,
        TiktokOverLimitDownloadEpisodeCount = keep,
    };

    [Fact]
    public void Download_selection_keeps_the_first_120_when_the_range_is_all()
    {
        OverLimitEpisodePolicy.ResolveDownloadSelection("all", 154, Settings())
            .Should().Be("1-120");
        OverLimitEpisodePolicy.ResolveDownloadSelection("1-5", 154, Settings())
            .Should().Be("1-5");
        OverLimitEpisodePolicy.ResolveDownloadSelection("all", 154, Settings(enabled: false))
            .Should().Be("all");
        OverLimitEpisodePolicy.ResolveDownloadSelection("all", 80, Settings())
            .Should().Be("all");
    }

    [Fact]
    public void Download_selection_uses_the_global_maximum_episode_count()
    {
        var settings = Settings(keep: 150);
        OverLimitEpisodePolicy.ResolveDownloadSelection("all", 180, settings)
            .Should().Be("1-150");
        OverLimitEpisodePolicy.ResolveKeepCount(settings).Should().Be(150);
    }

    [Fact]
    public void Existing_project_with_more_than_the_keep_count_is_marked_as_the_first_episodes()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "over-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            foreach (var episode in new[] { 1, 2, 3, 4 })
                File.WriteAllBytes(Path.Combine(projectDir, $"第{episode}集.mp4"), [1, 2, 3]);
            File.WriteAllText(
                Path.Combine(projectDir, "shortdrama-project.json"),
                JsonSerializer.Serialize(new { episodeCount = 4 }));
            File.WriteAllText(
                Path.Combine(projectDir, ".weixin-channel-download-state.json"),
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    video_count = 4,
                    episodes = "all",
                    failures = Array.Empty<string>(),
                }));

            OverLimitEpisodePolicy.EnsureTruncated(projectDir, Settings(keep: 2));

            OverLimitEpisodePolicy.TryReadKeepCount(projectDir, out var keep).Should().BeTrue();
            keep.Should().Be(2);
            ProjectWorkspaceService.ResolveSourceEpisodeCount(projectDir).Should().Be(2);
            ProjectVideoResolver.ResolveSourceVideos(projectDir).Should().HaveCount(2);
            ProjectVideoResolver.ResolveSourceVideos(projectDir, applyOverLimitSlice: false).Should().HaveCount(4);
        }
        finally
        {
            if (Directory.Exists(projectDir))
                Directory.Delete(projectDir, recursive: true);
        }
    }
}
