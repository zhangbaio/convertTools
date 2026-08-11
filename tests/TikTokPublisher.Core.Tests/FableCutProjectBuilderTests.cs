using System.Text;
using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Services.ProjectImages.FableCut;

namespace TikTokPublisher.Core.Tests;

public sealed class FableCutProjectBuilderTests
{
    [Fact]
    public void Build_uses_real_episode_number_and_source_style_asset_names()
    {
        var project = Build(
            videoName: "第 027 集_终局.mp4",
            episodeIndex: 3,
            cues: [new FableCutSubtitleCue(0, 3_000, "你终于来了。")]);

        project.Name.Should().Be("测试剧-第27集");
        project.Media.Should().HaveCount(16);
        project.Media[0].Id.Should().Be("media-episode");
        project.Media[0].Name.Should().Be("终局.mp4");
        project.Media.Should().Contain(item => item.Name == "主镜头_01.mp4");
        project.Media.Should().Contain(item => item.Name == "背景音乐_情绪推进.wav");

        var fallback = Build("没有集号.mov", episodeIndex: 6);
        fallback.Name.Should().Be("测试剧-第6集");
        fallback.Media[0].Name.Should().Be("没有集号.mov");
    }

    [Fact]
    public void Build_is_deterministic_and_primary_shots_are_uneven_but_cover_the_video()
    {
        var first = Build("第1集_样片.mp4", clipCount: 2);
        var second = Build("第1集_样片.mp4", clipCount: 2);

        JsonSerializer.Serialize(first).Should().Be(JsonSerializer.Serialize(second));

        var videoClips = first.Clips.Where(clip => clip.Track == "V1").ToArray();
        var sourceAudioClips = first.Clips.Where(clip => clip.Track == "A1").ToArray();
        videoClips.Should().HaveCount(FableCutProjectBuilder.MinimumClipCount);
        sourceAudioClips.Should().HaveCount(videoClips.Length);
        videoClips.Select(clip => Math.Round(clip.Duration, 6)).Distinct().Should().HaveCountGreaterThan(2);
        videoClips[0].Start.Should().Be(0);
        (videoClips[^1].Start + videoClips[^1].Duration).Should().BeApproximately(120, 0.000_001);

        for (var index = 0; index < videoClips.Length; index++)
        {
            sourceAudioClips[index].Start.Should().Be(videoClips[index].Start);
            sourceAudioClips[index].Duration.Should().Be(videoClips[index].Duration);
            sourceAudioClips[index].LinkGroup.Should().Be(videoClips[index].LinkGroup);
        }

        videoClips[2].Name.Should().MatchRegex(@"^sora2_\d{4}_03$");
        sourceAudioClips[2].Name.Should().Be(videoClips[2].Name);
        videoClips[0].Name.Should().MatchRegex(@"^素材_01_\d\.\dX$");
        sourceAudioClips[0].Name.Should().Be("素材_01");
    }

    [Fact]
    public void Build_creates_dense_bounded_subtitles_and_all_editor_tracks()
    {
        var longText = string.Concat(Enumerable.Repeat("甲乙丙丁戊己庚辛壬", 100));
        var project = Build(
            "第8集.mp4",
            duration: 60,
            cues: [new FableCutSubtitleCue(0, 60_000, longText)]);

        var trackNames = project.Clips.Select(clip => clip.Track).Distinct().ToArray();
        trackNames.Should().Contain(["V1", "A1", "V2", "A2", "V3", "A3", "A4"]);

        var subtitles = project.Clips.Where(clip => clip.Track == "V2").ToArray();
        subtitles.Should().HaveCount(40, "a 60-second video is capped at 40 subtitle blocks");
        subtitles.Should().OnlyContain(clip => clip.Name.EnumerateRunes().Count() <= 9);
        subtitles.Should().OnlyContain(clip => clip.Duration <= 2.2 + 0.000_001);
        subtitles.Should().OnlyContain(clip =>
            clip.Props.ContainsKey("opacity") && Equals(clip.Props["opacity"], 0));

        project.Clips.Count(clip => clip.Track == "A2").Should().Be(subtitles.Length);
        project.Clips.Count(clip => clip.Track == "A3").Should().Be(2);
        project.Clips.Count(clip => clip.Track == "A4").Should().Be(4);
        project.Markers.Should().HaveCount(8);
    }

    [Fact]
    public void BuildJson_emits_fablecut_marker_schema_and_utf8_payload()
    {
        var path = Path.Combine("D:\\素材", "第12集_春天.mp4");
        var json = FableCutProjectBuilder.BuildJson(
            path,
            "春日故事",
            episodeIndex: 1,
            durationSeconds: 30,
            width: 1080,
            height: 1920,
            clipCount: 24,
            subtitleCues: [new FableCutSubtitleCue(0, 2_000, "春天来了。")]);
        var utf8 = FableCutProjectBuilder.BuildUtf8Json(
            path,
            "春日故事",
            episodeIndex: 1,
            durationSeconds: 30,
            width: 1080,
            height: 1920,
            clipCount: 24,
            subtitleCues: [new FableCutSubtitleCue(0, 2_000, "春天来了。")]);

        Encoding.UTF8.GetString(utf8).Should().Be(json);
        json.Should().Contain("春日故事-第12集");
        using var document = JsonDocument.Parse(json);
        var marker = document.RootElement.GetProperty("markers")[0];
        marker.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["t", "label"]);
        marker.TryGetProperty("time", out _).Should().BeFalse();
        marker.TryGetProperty("name", out _).Should().BeFalse();

        var clip = document.RootElement.GetProperty("clips")[0];
        clip.TryGetProperty("mediaId", out _).Should().BeTrue();
        clip.TryGetProperty("linkGroup", out _).Should().BeTrue();
        clip.TryGetProperty("in", out _).Should().BeTrue();
    }

    [Fact]
    public void Build_without_asr_cues_still_adds_fallback_dialogue_audio()
    {
        var project = Build("第2集.mp4", cues: []);

        project.Clips.Should().NotContain(clip => clip.Track == "V2");
        project.Clips.Count(clip => clip.Track == "A2").Should().Be(8);
    }

    private static FableCutProject Build(
        string videoName,
        int episodeIndex = 1,
        double duration = 120,
        int clipCount = 24,
        IReadOnlyList<FableCutSubtitleCue>? cues = null) =>
        FableCutProjectBuilder.Build(
            Path.Combine("D:\\短剧", videoName),
            "测试剧",
            episodeIndex,
            duration,
            width: 1080,
            height: 1920,
            clipCount,
            cues ?? [new FableCutSubtitleCue(0, 4_000, "这是默认测试对白。")]);
}
