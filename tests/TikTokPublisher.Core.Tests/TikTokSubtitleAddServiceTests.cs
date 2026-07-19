using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokSubtitleAddServiceTests
{
    [Fact]
    public void ParseSrt_accepts_multiline_text_and_comma_timestamps()
    {
        const string srt = """
            1
            00:00:01,250 --> 00:00:03,500
            Hello
            world

            2
            00:00:04,000 --> 00:00:05,125
            Next line
            """;

        var cues = TikTokSubtitleAddService.ParseSrtForTest(srt);

        cues.Should().HaveCount(2);
        cues[0].Start.Should().Be(TimeSpan.FromMilliseconds(1250));
        cues[0].Text.Should().Be("Hello\nworld");
        cues[1].End.Should().Be(TimeSpan.FromMilliseconds(5125));
    }

    [Fact]
    public void RestoreCleanDubbedVideo_replaces_an_already_subtitled_video_with_the_clean_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "subtitle-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            var staging = Path.Combine(root, "workflow", "project", "tiktok_upload_videos");
            Directory.CreateDirectory(staging);
            var video = Path.Combine(staging, "episode.mp4");
            var clean = Path.Combine(root, "clean.mp4");
            File.WriteAllText(video, "already-subtitled");
            File.WriteAllText(clean, "clean-dubbed-video");
            var episode = new VideoLocalizationEpisodeState
            {
                CleanDubbedVideoPath = clean,
                SubtitlesBurned = true,
            };

            TikTokSubtitleAddService.RestoreCleanDubbedVideo(video, episode);

            File.ReadAllText(video).Should().Be("clean-dubbed-video");
            episode.SubtitlesBurned.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
