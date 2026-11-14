using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Core.Tests;

public sealed class VideoDedupTests
{
    [Fact]
    public void Step_order_puts_dedup_after_download_and_retires_source_deletion()
    {
        QueueStepRegistry.All.Select(step => step.Key).Should().ContainInOrder(
            QueueStepKeys.Download,
            QueueStepKeys.VideoDedup,
            QueueStepKeys.RewriteInfo);
        QueueStepRegistry.All.Select(step => step.Key).Should().NotContain(QueueStepKeys.DeleteSourceVideos);
        QueueStepRegistry.UserSelectable.Select(step => step.Key).Should().Contain(QueueStepKeys.VideoDedup);
        QueueStepRegistry.LabelOf(QueueStepKeys.VideoDedup).Should().Be("视频去重");

        var options = QueueRunOptions.FromDictionary(new Dictionary<string, object?>
        {
            ["enabled_steps"] = new List<object?> { "download", "delete_source_videos", "video_dedup" },
        });
        options.EnabledSteps.Should().Equal(QueueStepKeys.Download, QueueStepKeys.VideoDedup);
    }

    [Fact]
    public void Importer_reads_the_processor_default_preset()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "name": "默认方案",
              "encodingPreference": "HardwareFirst",
              "parallel": 2,
              "minimumDurationSeconds": null,
              "maximumDurationSeconds": null,
              "steps": [
                { "type": "fps", "fps": 30 },
                { "type": "videoEncode", "codec": "h264", "bitrateMode": "Variable", "bitrateKbps": 4200 },
                { "type": "audioEncode", "codec": "aac" }
              ]
            }
            """;

        var preset = VideoDedupPresetImporter.FromProcessorJson(json);

        preset.Name.Should().Be("默认方案");
        preset.EncodingPreference.Should().Be(VideoDedupPreset.EncodingHardwareFirst);
        preset.Parallel.Should().Be(2);
        preset.Fps.Should().Be(30);
        preset.VideoCodec.Should().Be("h264");
        preset.BitrateMode.Should().Be(VideoDedupPreset.BitrateVbr);
        preset.VideoBitrateMbps.Should().Be(4.2);
        preset.AudioCodec.Should().Be("aac");
        preset.HasDurationLimit.Should().BeFalse();
    }

    [Fact]
    public void Importer_reads_transition_candidates_quality_mode_and_enabled_effects()
    {
        const string json = """
            {
              "name": "视频号",
              "effects": {
                "basic": {
                  "gamma": { "value": 1.2, "enabled": true, "amount": 50 },
                  "tint": { "value": 30, "enabled": false, "amount": 100 }
                },
                "style": {
                  "curves": {
                    "master": [ { "x": 0, "y": 0 }, { "x": 0.5, "y": 0.6 }, { "x": 1, "y": 1 } ],
                    "enabled": true, "amount": 80
                  }
                },
                "spatial": {
                  "vignette": { "radius": 60, "feather": 40, "enabled": true, "amount": 70 },
                  "grain": { "size": 0.15, "density": 50, "enabled": true, "amount": 40 }
                }
              },
              "transitions": { "enabled": true, "candidates": [ "FadeBlack", "Zoom" ] },
              "steps": [
                { "type": "videoEncode", "codec": "h264", "bitrateMode": "Quality", "quality": 24 }
              ]
            }
            """;

        var preset = VideoDedupPresetImporter.FromProcessorJson(json);

        preset.TransitionsEnabled.Should().BeTrue();
        preset.TransitionFadeBlack.Should().BeTrue();
        preset.TransitionZoom.Should().BeTrue();
        preset.TransitionDissolve.Should().BeFalse();
        preset.BitrateMode.Should().Be(VideoDedupPreset.BitrateCrf);
        preset.Crf.Should().Be(24);
        preset.Effects.Gamma.Should().BeApproximately(20, 0.001);
        preset.Effects.Tint.Should().Be(0);
        preset.Effects.CurveAmount.Should().Be(80);
        preset.Effects.CurveMaster.Should().HaveCount(3);
        preset.Effects.VignetteAmount.Should().Be(70);
        preset.Effects.VignetteRadius.Should().Be(60);
        preset.Effects.GrainAmount.Should().Be(40);
        preset.Effects.GrainSize.Should().BeApproximately(15, 0.001);
    }

    [Fact]
    public void Speed_change_runs_before_fps_and_frame_drop_so_audio_stays_in_sync()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.SpeedFactor = 1.1;
        preset.FrameDropPercent = 8;
        preset.Normalize();
        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(60, 1080, 1920, true));

        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 5));

        var speed = joined.IndexOf("setpts=PTS/1.1", StringComparison.Ordinal);
        var fps = joined.IndexOf("fps=30", StringComparison.Ordinal);
        var drop = joined.IndexOf("select=", StringComparison.Ordinal);
        speed.Should().BeGreaterThan(0);
        fps.Should().BeGreaterThan(speed);
        drop.Should().BeGreaterThan(fps);
        joined.Should().Contain("atempo=1.2 ");
        joined.Should().Contain("aresample=48000");
        VideoDedupPlanner.ProcessedDuration(60, preset).Should().BeApproximately(60 / 1.1 * 11 / 12, 0.0001);
    }

    [Fact]
    public void Frame_drop_picks_one_seeded_position_per_block()
    {
        var first = VideoDedupPlanner.FrameDropFilter(8, seed: 1);
        var second = VideoDedupPlanner.FrameDropFilter(8, seed: 2);

        first.Should().Contain("mod(n\\,12)");
        first.Should().Contain("floor(n/12)");
        first.Should().EndWith("setpts=N/FRAME_RATE/TB");
        second.Should().NotBe(first);
    }

    [Fact]
    public void Duration_limit_averages_the_whole_timeline_across_source_episodes()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.MinimumDurationSeconds = 60;
        preset.MaximumDurationSeconds = 100;
        preset.Normalize();

        var groups = VideoDedupPlanner.Plan([80, 80, 20], preset);

        groups.Should().HaveCount(2);
        groups[0].Sum(slice => slice.Length).Should().Be(90);
        groups[1].Sum(slice => slice.Length).Should().Be(90);
        groups[0].Select(slice => slice.ClipIndex).Should().Equal(0, 1);
        groups[1].Select(slice => slice.ClipIndex).Should().Equal(1, 2);
    }

    [Fact]
    public void Long_episode_is_split_on_the_pre_trim_duration_and_a_short_tail_stays()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.MinimumDurationSeconds = 60;
        preset.MaximumDurationSeconds = 130;
        preset.TrimStartSeconds = 2;
        preset.TrimEndSeconds = 2;
        preset.Normalize();

        var split = VideoDedupPlanner.Plan([214.321], preset);
        split.Should().HaveCount(2);
        split[0][0].StartSeconds.Should().Be(2);
        split[0][0].EndSeconds.Should().BeApproximately(105.1605, 0.001);
        split[1][0].StartSeconds.Should().BeApproximately(109.1605, 0.001);
        split[1][0].EndSeconds.Should().BeApproximately(212.321, 0.001);

        var even = VideoDedupPlanner.Plan([140], preset);
        even.Should().HaveCount(2);
        even[0][0].StartSeconds.Should().Be(2);
        even[0][0].EndSeconds.Should().Be(68);
        even[1][0].StartSeconds.Should().Be(72);
        even[1][0].EndSeconds.Should().Be(138);

        var clip = new VideoDedupClip("long.mp4", new VideoDedupProbe(214.321, 1080, 1920, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], split[1], "out.mp4", "libx264", seed: 1));
        joined.Should().Contain("-ss 109.1605 -t 104.1605 -i long.mp4");
        joined.Should().Contain("trim=start=0:end=103.1605");
        joined.Should().Contain("atrim=start=0:end=103.1605");
        joined.Should().NotContain("trim=start=109.1605");
    }

    [Fact]
    public void Two_slices_of_one_file_open_separate_inputs_when_only_the_later_one_seeks()
    {
        var preset = VideoDedupPreset.CreateDefault().NormalizedCopy();
        var clip = new VideoDedupClip("long.mp4", new VideoDedupProbe(200, 1080, 1920, true));
        VideoDedupSlice[] slices = [new(0, 0, 50), new(0, 130, 180)];

        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], slices, "out.mp4", "libx264", seed: 1));

        joined.Should().Contain("-i long.mp4 -ss 130 -t 51 -i long.mp4");
        joined.Should().Contain("[0:v]");
        joined.Should().Contain("[1:v]");
        joined.Should().NotContain("trim=start=130");
    }

    [Fact]
    public void A_tail_shorter_than_fifteen_seconds_after_speed_joins_the_previous_episode()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.MinimumDurationSeconds = 30;
        preset.MaximumDurationSeconds = 40;
        preset.TrimStartSeconds = 2;
        preset.TrimEndSeconds = 2;
        preset.SpeedFactor = 1.3;
        preset.Normalize();

        var shortTail = VideoDedupPlanner.Plan([49.9], preset);
        shortTail.Should().ContainSingle();
        shortTail[0].Should().ContainSingle();
        shortTail[0][0].StartSeconds.Should().Be(2);
        shortTail[0][0].EndSeconds.Should().BeApproximately(47.9, 0.001);
        (shortTail[0].Sum(slice => slice.Length) / preset.SpeedFactor).Should().BeGreaterThanOrEqualTo(15);

        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([64.7], preset), preset)
            .Should().Contain("第 1 集预计 46.7 秒，超过最大 40 秒");
    }

    [Fact]
    public void Duration_limit_trims_a_fitting_episode_after_it_is_arranged()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.MinimumDurationSeconds = 60;
        preset.MaximumDurationSeconds = 130;
        preset.TrimStartSeconds = 2;
        preset.TrimEndSeconds = 2;
        preset.Normalize();

        var groups = VideoDedupPlanner.Plan([80], preset);

        groups.Should().HaveCount(1);
        groups[0][0].StartSeconds.Should().Be(2);
        groups[0][0].EndSeconds.Should().Be(78);
    }

    [Fact]
    public void Partial_duration_limit_does_not_merge_episodes_and_cannot_be_saved()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.MinimumDurationSeconds = 60;
        preset.Normalize();

        preset.HasDurationLimit.Should().BeFalse();
        preset.DurationRangeError().Should().Contain("同时填写");
        VideoDedupPlanner.Plan([20, 20, 20], preset).Should().HaveCount(3);

        preset.MaximumDurationSeconds = 40;
        preset.Normalize();
        preset.DurationRangeError().Should().Contain("不能大于");
    }

    [Fact]
    public void Duration_check_passes_when_every_planned_episode_is_inside_the_range()
    {
        var preset = LimitedPreset(35, 65);
        var plan = VideoDedupPlanner.Plan([40, 50, 60], preset);

        VideoDedupPlanner.DescribeDurationMismatch(plan, preset).Should().BeNull();
        plan.Should().HaveCount(3);
        plan.Select(group => group.Sum(slice => slice.Length)).Should().AllBeEquivalentTo(50d);
        VideoDedupPlanner.DescribeDurationCheckPassed(3, plan, preset)
            .Should().Be("分集计算通过：3 个原片共 2.5 分钟，平均分成 3 集，每集 50 秒，在 35–65 秒内。");
    }

    [Fact]
    public void Duration_check_rounds_to_a_tenth_and_accepts_the_exact_limits()
    {
        var preset = LimitedPreset(35, 65);

        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([34.96], preset), preset).Should().BeNull();
        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([65.04], preset), preset).Should().BeNull();
        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([34.94], preset), preset)
            .Should().Contain("第 1 集预计 34.9 秒，短于最小 35 秒");
        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([65.05], preset), preset)
            .Should().Contain("第 1 集预计 65.1 秒，超过最大 65 秒");
    }

    [Fact]
    public void Duration_check_averages_a_short_source_and_stops_when_no_split_fits()
    {
        var merged = LimitedPreset(35, 65);
        var plan = VideoDedupPlanner.Plan([60, 20], merged);
        VideoDedupPlanner.DescribeDurationMismatch(plan, merged).Should().BeNull();
        plan.Should().HaveCount(2);
        plan.Select(group => group.Sum(slice => slice.Length)).Should().AllBeEquivalentTo(40d);

        var impossible = LimitedPreset(35, 50);
        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([50, 10], impossible), impossible)
            .Should().Be("视频去重已停止，不会开始编码。按 35–50 秒平均分集，第 1 集预计 30 秒，短于最小 35 秒；第 2 集预计 30 秒，短于最小 35 秒。");

        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([66.5], merged), merged)
            .Should().Contain("第 1 集预计 66.5 秒，超过最大 65 秒");
        var rescued = VideoDedupPlanner.Plan([66.5, 72.8], merged);
        VideoDedupPlanner.DescribeDurationMismatch(rescued, merged).Should().BeNull();
        rescued.Should().HaveCount(3);
    }

    [Fact]
    public void Duration_check_counts_speed_frame_drop_and_transition_overlap()
    {
        var sped = LimitedPreset(35, 65);
        sped.SpeedFactor = 1.2;
        sped.Normalize();
        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([40], sped), sped)
            .Should().Contain("第 1 集预计 33.3 秒，短于最小 35 秒");

        var dropped = LimitedPreset(35, 65);
        dropped.FrameDropPercent = 8;
        dropped.Normalize();
        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([36], dropped), dropped)
            .Should().Contain("第 1 集预计 33 秒，短于最小 35 秒");

        var transition = LimitedPreset(72, 100);
        transition.TransitionsEnabled = true;
        transition.TransitionFadeBlack = true;
        transition.Normalize();
        var joined = VideoDedupPlanner.Plan([36, 36], transition);
        VideoDedupPlanner.DescribeDurationMismatch(joined, transition, ["a.mp4", "b.mp4"])
            .Should().Contain("短于最小 72 秒");
    }

    [Fact]
    public void Transition_overlap_borrows_from_the_neighbor_to_stay_inside_the_range()
    {
        var preset = LimitedPreset(54.8, 70);
        preset.TransitionsEnabled = true;
        preset.TransitionFadeBlack = true;
        preset.Normalize();

        var plan = VideoDedupPlanner.Plan([50, 60], preset);

        VideoDedupPlanner.DescribeDurationMismatch(plan, preset).Should().BeNull();
        plan.Should().HaveCount(2);
        plan[0].Sum(slice => slice.Length).Should().BeGreaterThan(55);
        plan[1].Sum(slice => slice.Length).Should().BeLessThan(55);
    }

    [Fact]
    public void Duration_check_lists_the_first_twelve_episodes_when_more_fail()
    {
        var preset = LimitedPreset(60, 64);
        var durations = Enumerable.Repeat(59.6, 13).ToArray();

        var message = VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan(durations, preset), preset);

        message.Should().Contain("第 1 集预计 59.6 秒，短于最小 60 秒");
        message.Should().Contain("第 12 集预计 59.6 秒，短于最小 60 秒");
        message.Should().Contain("等 13 集");
        message.Should().NotContain("第 13 集预计");
        VideoDedupPlanner.FormatDurationStop(message!).Should().StartWith(
            "请调整单集时长，或改原片后再重跑。" + Environment.NewLine);

        var averaged = LimitedPreset(35, 50);
        averaged.SpeedFactor = 1.3;
        averaged.Normalize();
        var merged = VideoDedupPlanner.Plan(Enumerable.Repeat(40d, 13).ToArray(), averaged);
        VideoDedupPlanner.DescribeDurationMismatch(merged, averaged).Should().BeNull();
        merged.Should().HaveCount(9);
    }

    [Fact]
    public void Duration_check_is_skipped_when_the_range_is_unset()
    {
        var preset = VideoDedupPreset.CreateDefault().NormalizedCopy();

        VideoDedupPlanner.DescribeDurationMismatch(VideoDedupPlanner.Plan([5], preset), preset).Should().BeNull();
    }

    private static VideoDedupPreset LimitedPreset(double minimum, double maximum)
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.MinimumDurationSeconds = minimum;
        preset.MaximumDurationSeconds = maximum;
        preset.Normalize();
        return preset;
    }

    [Fact]
    public void Default_encode_arguments_use_h264_vbr_and_aac()
    {
        var preset = VideoDedupPreset.CreateDefault().NormalizedCopy();
        var clip = new VideoDedupClip(
            "episode-1.mp4",
            new VideoDedupProbe(12, 1080, 1920, true));

        var args = VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 3);
        var joined = string.Join(" ", args);

        joined.Should().Contain("-c:v libx264");
        joined.Should().Contain("-b:v 2000k");
        joined.Should().Contain("-r 30");
        joined.Should().Contain("-c:a aac");
        joined.Should().Contain("-profile:v main");
        joined.Should().Contain("setparams=range=tv:color_primaries=bt709");
        joined.Should().Contain("aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo");
        joined.Should().NotContain("scale=");
        args[^1].Should().Be("out.mp4");
    }

    [Fact]
    public void Landscape_source_swaps_the_single_resolution()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.PortraitWidth = 720;
        preset.PortraitHeight = 1280;
        preset.LandscapeWidth = 640;
        preset.LandscapeHeight = 360;
        preset.CropTopPercent = 2;
        preset.CropBottomPercent = 6;
        preset.Normalize();

        preset.CropTopPercent.Should().Be(6);
        preset.CropBottomPercent.Should().Be(6);
        preset.LandscapeWidth.Should().Be(1280);
        preset.LandscapeHeight.Should().Be(720);

        preset.PortraitWidth = 1080;
        preset.PortraitHeight = 1920;
        var clip = new VideoDedupClip("wide.mp4", new VideoDedupProbe(12, 1920, 1080, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));
        joined.Should().Contain("scale=1920:1080:flags=bicubic");
        joined.Should().NotContain("force_original_aspect_ratio");
    }

    [Fact]
    public void Crop_below_1080_scales_back_to_the_source()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.PortraitWidth = 0;
        preset.PortraitHeight = 0;
        preset.CropTopPercent = 3;
        preset.CropBottomPercent = 3;
        preset.CropLeftPercent = 3;
        preset.CropRightPercent = 3;
        preset.Normalize();

        var frame = VideoDedupPlanner.ResolveUploadFrame(1080, 1920, preset);
        frame.Width.Should().Be(1080);
        frame.Height.Should().Be(1920);
        frame.Scaled.Should().BeTrue();

        var clip = new VideoDedupClip("tall.mp4", new VideoDedupProbe(42, 1080, 1920, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));
        joined.Should().Contain("scale=1080:1920:flags=bicubic");
        joined.Should().Contain("crop=trunc(iw*");
    }

    [Fact]
    public void Mixed_frame_sizes_in_one_episode_scale_to_one_upload_frame()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.PortraitWidth = 0;
        preset.PortraitHeight = 0;
        preset.CropTopPercent = 3;
        preset.CropBottomPercent = 3;
        preset.CropLeftPercent = 3;
        preset.CropRightPercent = 3;
        preset.TransitionsEnabled = true;
        preset.TransitionDissolve = true;
        preset.Normalize();

        var small = new VideoDedupClip("small.mp4", new VideoDedupProbe(12, 720, 1282, true));
        var tall = new VideoDedupClip("tall.mp4", new VideoDedupProbe(12, 1080, 1922, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [small, tall], "out.mp4", "libx264", seed: 1));
        joined.Split("scale=1080:1920:").Length.Should().Be(3);
        joined.Should().NotContain("scale=1080:1922:");
        joined.Should().Contain("xfade=");

        preset.TransitionsEnabled = false;
        var concat = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [small, tall], "out.mp4", "libx264", seed: 1));
        concat.Split("scale=1080:1920:").Length.Should().Be(3);
        concat.Should().Contain("concat=n=2");
        concat.Should().NotContain("xfade=");

        var uniform = string.Join(" ", VideoDedupPlanner.BuildArguments(
            preset,
            [
                new VideoDedupClip("a.mp4", new VideoDedupProbe(12, 1080, 1922, true)),
                new VideoDedupClip("b.mp4", new VideoDedupProbe(12, 1080, 1922, true)),
            ],
            "out.mp4",
            "libx264",
            seed: 1));
        uniform.Should().Contain("scale=1080:1922:");
        uniform.Should().NotContain("scale=1080:1920:");

        var wide = string.Join(" ", VideoDedupPlanner.BuildArguments(
            preset,
            [
                new VideoDedupClip("wide-small.mp4", new VideoDedupProbe(12, 1280, 720, true)),
                new VideoDedupClip("wide.mp4", new VideoDedupProbe(12, 1922, 1080, true)),
            ],
            "out.mp4",
            "libx264",
            seed: 1));
        wide.Split("scale=1920:1080:").Length.Should().Be(3);
        wide.Should().NotContain("scale=1922:1080:");

        VideoDedupClip[] clips =
        [
            new("p1.mp4", new VideoDedupProbe(12, 720, 1282, true)),
            new("p2.mp4", new VideoDedupProbe(12, 720, 1282, true)),
            new("wide.mp4", new VideoDedupProbe(12, 1922, 1080, true)),
        ];
        VideoDedupPlanner.SharedEpisodeFrame(
            clips,
            [new VideoDedupSlice(0, 0, 12), new VideoDedupSlice(1, 0, 12), new VideoDedupSlice(2, 0, 12)],
            preset).Should().Be(new VideoDedupPlanner.UploadFrame(1080, 1920, true));
        VideoDedupPlanner.SharedEpisodeFrame(
            clips,
            [new VideoDedupSlice(2, 0, 12), new VideoDedupSlice(0, 0, 12)],
            preset).Should().Be(new VideoDedupPlanner.UploadFrame(1920, 1080, true));
    }

    [Fact]
    public void Source_below_1080_scales_to_the_upload_frame()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.PortraitWidth = 0;
        preset.PortraitHeight = 0;
        preset.Normalize();

        var portrait = VideoDedupPlanner.ResolveUploadFrame(720, 1280, preset);
        portrait.Width.Should().Be(1080);
        portrait.Height.Should().Be(1920);

        var landscape = VideoDedupPlanner.ResolveUploadFrame(1280, 720, preset);
        landscape.Width.Should().Be(1920);
        landscape.Height.Should().Be(1080);

        var smallTarget = VideoDedupPreset.CreateDefault();
        smallTarget.PortraitWidth = 720;
        smallTarget.PortraitHeight = 1280;
        smallTarget.Normalize();
        var raised = VideoDedupPlanner.ResolveUploadFrame(1080, 1920, smallTarget);
        raised.Width.Should().Be(1080);
        raised.Height.Should().Be(1920);
        raised.Scaled.Should().BeFalse();
    }

    [Fact]
    public void Original_resolution_keeps_a_cropped_frame_that_stays_at_least_1080()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.PortraitWidth = 0;
        preset.PortraitHeight = 0;
        preset.CropTopPercent = 4;
        preset.Normalize();

        preset.KeepsOriginalResolution.Should().BeTrue();
        preset.LandscapeWidth.Should().Be(0);
        preset.LandscapeHeight.Should().Be(0);

        var clip = new VideoDedupClip("wide.mp4", new VideoDedupProbe(12, 2560, 1440, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));
        joined.Should().NotContain("scale=");
        joined.Should().Contain("crop=trunc(iw*");
        joined.Should().Contain("setparams=range=tv");
        joined.Should().Contain("fps=30");
    }

    [Fact]
    public void Encoder_arguments_fit_each_vendor_and_rate_mode()
    {
        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, true));
        string Build(VideoDedupPreset preset, string encoder) =>
            string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", encoder, seed: 1));

        var vbr = VideoDedupPreset.CreateDefault().NormalizedCopy();
        vbr.VideoBitrateMbps = 2;
        Build(vbr, "libx264").Should().Contain("-b:v 2000k -maxrate 2900k -bufsize 4000k");
        Build(vbr, "libx265").Should().Contain("-bufsize 4000k").And.Contain("-tag:v hvc1");
        Build(vbr, "hevc_amf").Should().Contain("-tag:v hvc1");
        Build(vbr, "hevc_nvenc").Should().Contain("-tag:v hvc1");
        Build(vbr, "h264_amf").Should().NotContain("-tag:v");
        Build(vbr, "h264_qsv").Should().Contain("-pix_fmt nv12").And.NotContain("-pix_fmt yuv420p");
        Build(vbr, "h264_amf").Should().Contain("-pix_fmt yuv420p");

        var crf = VideoDedupPreset.CreateDefault();
        crf.BitrateMode = VideoDedupPreset.BitrateCrf;
        crf.Crf = 23;
        crf.Normalize();
        Build(crf, "h264_amf").Should().Contain("-rc cqp -qp_i 25 -qp_p 27 -qp_b 27");
        Build(crf, "libx264").Should().Contain("-crf 23");
        Build(crf, "h264_nvenc").Should().Contain("-cq 23");
        Build(crf, "h264_qsv").Should().Contain("-global_quality 23");
    }

    [Fact]
    public void Encoder_probe_uses_the_real_encoder_arguments()
    {
        var preset = VideoDedupPreset.CreateDefault().NormalizedCopy();
        var probe = string.Join(" ", VideoDedupPlanner.BuildProbeArguments(preset, "h264_nvenc"));
        probe.Should().Contain("testsrc=size=1080x1920");
        probe.Should().Contain("-c:v h264_nvenc");
        probe.Should().Contain("-b_ref_mode middle");
        probe.Should().Contain("-profile:v main");
        probe.Should().NotContain("-c:a");
        probe.Should().EndWith("-f null -");
    }

    [Fact]
    public void Hardware_arguments_follow_the_vendor_encoder()
    {
        var preset = VideoDedupPreset.CreateDefault().NormalizedCopy();
        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, true));

        var nvenc = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "h264_nvenc", seed: 1));
        nvenc.Should().Contain("-c:v h264_nvenc");
        nvenc.Should().Contain("-preset p2");
        nvenc.Should().Contain("-rc-lookahead 0");
        nvenc.Should().Contain("-gpu any");
        nvenc.Should().Contain("-rc vbr");
        nvenc.Should().Contain("-profile:v main");

        var amf = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "h264_amf", seed: 1));
        amf.Should().Contain("-c:v h264_amf");
        amf.Should().Contain("-usage transcoding");
        amf.Should().Contain("-quality speed");
        amf.Should().Contain("-rc vbr_peak");
        amf.Should().NotContain("-preset");

        var qsv = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "h264_qsv", seed: 1));
        qsv.Should().Contain("-c:v h264_qsv");
        qsv.Should().Contain("-preset veryfast");
        qsv.Should().Contain("-b:v 2000k");
        qsv.Should().NotContain("-gpu");
    }

    [Fact]
    public async Task Auto_encoder_uses_the_first_card_that_can_encode()
    {
        VideoDedupService.EncoderWorksForTests = (encoder, _) =>
            Task.FromResult(encoder is "h264_amf");
        try
        {
            var choice = await VideoDedupService.ResolveEncoderForTests(
                VideoDedupPreset.CreateDefault(),
                CancellationToken.None);

            choice.Encoder.Should().Be("h264_amf");
            choice.Device.Should().Be("AMD");
        }
        finally
        {
            VideoDedupService.EncoderWorksForTests = null;
        }
    }

    [Fact]
    public async Task Auto_encoder_uses_cpu_when_no_graphics_card_can_encode()
    {
        VideoDedupService.EncoderWorksForTests = (_, _) => Task.FromResult(false);
        try
        {
            var choice = await VideoDedupService.ResolveEncoderForTests(
                VideoDedupPreset.CreateDefault(),
                CancellationToken.None);

            choice.Encoder.Should().Be("libx264");
            choice.Device.Should().Be("CPU");
            choice.Note.Should().Contain("NVIDIA").And.Contain("AMD");
        }
        finally
        {
            VideoDedupService.EncoderWorksForTests = null;
        }
    }

    [Fact]
    public void Saved_hardware_only_preference_becomes_automatic()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.EncodingPreference = "HardwareOnly";
        preset.Normalize();

        preset.EncodingPreference.Should().Be(VideoDedupPreset.EncodingHardwareFirst);
    }

    [Fact]
    public async Task Explicit_nvidia_fails_when_that_card_cannot_encode()
    {
        VideoDedupService.EncoderWorksForTests = (encoder, _) =>
            Task.FromResult(encoder is "h264_amf");
        try
        {
            var preset = VideoDedupPreset.CreateDefault();
            preset.EncodingPreference = VideoDedupPreset.EncodingNvenc;
            var act = () => VideoDedupService.ResolveEncoderForTests(preset, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*NVIDIA*");
        }
        finally
        {
            VideoDedupService.EncoderWorksForTests = null;
        }
    }

    [Fact]
    public async Task Run_replaces_visible_videos_and_keeps_originals()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "01.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var staging = Path.Combine(
            ProjectWorkspaceService.ResolveWorkflowProjectDir(root),
            TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "old-第1集.mp4"), [7]);
        VideoDedupService.ProbeForTests = (_, _) =>
            Task.FromResult(new VideoDedupProbe(8, 720, 1280, true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (args, _) =>
        {
            File.WriteAllBytes(args[^1], [9, 9, 9, 9, 9]);
            return Task.CompletedTask;
        };
        try
        {
            var item = new QueueProjectItem { ProjectDir = root, EpisodeCount = 1 };
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1" };
            var logs = new List<string>();

            await VideoDedupService.RunAsync(item, account, force: false, logs.Add, CancellationToken.None);

            item.EpisodeCount.Should().Be(1);
            (await File.ReadAllBytesAsync(source)).Should().Equal([9, 9, 9, 9, 9]);
            var archived = Path.Combine(root, "source-originals", "01.mp4");
            (await File.ReadAllBytesAsync(archived)).Should().Equal([1, 2, 3, 4]);
            logs.Should().Contain(line => line.Contains("使用内置默认方案", StringComparison.Ordinal));
            logs.Should().Contain(line => line.Contains("去重操作：上裁边 0%，下裁边 0%，左裁边 0%，右裁边 0%", StringComparison.Ordinal) && line.Contains("编码器自动选择", StringComparison.Ordinal) && line.Contains("H.264", StringComparison.Ordinal) && line.Contains("VBR 2 Mbps", StringComparison.Ordinal));
            logs.Should().Contain(line => line.Contains("第 1/1 集开始去重：01.mp4（8 秒，720x1280） → 01.mp4，成片 1080×1920。", StringComparison.Ordinal));
            logs.Should().NotContain(line => line.Contains("集开始去重", StringComparison.Ordinal) && line.Contains("裁边", StringComparison.Ordinal));
            logs.Count(line => line.Contains("去重操作：", StringComparison.Ordinal)).Should().Be(1);
            logs.Should().Contain(line => line.Contains("第 1/1 集去重完成", StringComparison.Ordinal));
            logs.Should().Contain(line => line.Contains("已清除旧的上传副本", StringComparison.Ordinal));
            Directory.Exists(staging).Should().BeFalse();

            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(Path.Combine(staging, "kept-第1集.mp4"), [6]);
            logs.Clear();
            await VideoDedupService.RunAsync(item, account, force: false, logs.Add, CancellationToken.None);
            logs.Should().Contain(line => line.StartsWith("跳过", StringComparison.Ordinal));
            File.Exists(Path.Combine(staging, "kept-第1集.mp4")).Should().BeTrue();
        }
        finally
        {
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public async Task Duration_merge_replaces_visible_files_and_updates_episode_count()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "a.mp4"), [1, 1]);
        await File.WriteAllBytesAsync(Path.Combine(root, "b.mp4"), [2, 2, 2]);
        await File.WriteAllTextAsync(
            Path.Combine(root, ".weixin-channel-download-state.json"),
            """{"video_count":2}""");
        VideoDedupService.ProbeForTests = (_, _) =>
            Task.FromResult(new VideoDedupProbe(15, 1080, 1920, true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (args, _) =>
        {
            File.WriteAllBytes(args[^1], [8, 8, 8]);
            return Task.CompletedTask;
        };
        try
        {
            var preset = VideoDedupPreset.CreateDefault();
            preset.MinimumDurationSeconds = 20;
            preset.MaximumDurationSeconds = 40;
            var item = new QueueProjectItem { ProjectDir = root, EpisodeCount = 2, SourceEpisodeCount = 2 };
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1", VideoDedupPreset = preset };
            var logs = new List<string>();

            await VideoDedupService.RunAsync(item, account, force: false, logs.Add, CancellationToken.None);

            logs.Should().Contain(line => line.Contains("分集计算通过：2 个原片共 0.5 分钟，平均分成 1 集，每集 30 秒，在 20–40 秒内。", StringComparison.Ordinal));

            item.EpisodeCount.Should().Be(1);
            item.SourceEpisodeCount.Should().Be(2);
            File.Exists(Path.Combine(root, "a.mp4")).Should().BeFalse();
            File.Exists(Path.Combine(root, "b.mp4")).Should().BeFalse();
            (await File.ReadAllBytesAsync(Path.Combine(root, "01.mp4"))).Should().Equal([8, 8, 8]);
            (await File.ReadAllBytesAsync(Path.Combine(root, "source-originals", "a.mp4"))).Should().Equal([1, 1]);
            (await File.ReadAllBytesAsync(Path.Combine(root, "source-originals", "b.mp4"))).Should().Equal([2, 2, 2]);
            var state = await File.ReadAllTextAsync(Path.Combine(root, ".weixin-channel-download-state.json"));
            state.Should().Contain("\"video_count\": 1");
            state.Should().Contain("\"source_video_count\": 2");
        }
        finally
        {
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public async Task Duration_check_stops_before_encode_when_a_planned_episode_is_too_short()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "a.mp4");
        var second = Path.Combine(root, "b.mp4");
        await File.WriteAllBytesAsync(first, [1, 1]);
        await File.WriteAllBytesAsync(second, [2, 2, 2]);
        var encoded = 0;
        VideoDedupService.ProbeForTests = (path, _) =>
            Task.FromResult(new VideoDedupProbe(
                Path.GetFileName(path).StartsWith("b", StringComparison.OrdinalIgnoreCase) ? 10 : 50,
                1080,
                1920,
                true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (_, _) =>
        {
            encoded++;
            return Task.CompletedTask;
        };
        try
        {
            var preset = VideoDedupPreset.CreateDefault();
            preset.MinimumDurationSeconds = 35;
            preset.MaximumDurationSeconds = 50;
            var item = new QueueProjectItem { ProjectDir = root, EpisodeCount = 2 };
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1", VideoDedupPreset = preset };
            var logs = new List<string>();

            var act = () => VideoDedupService.RunAsync(item, account, force: false, logs.Add, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*第 1 集预计 30 秒，短于最小 35 秒*");
            encoded.Should().Be(0);
            item.EpisodeCount.Should().Be(2);
            (await File.ReadAllBytesAsync(first)).Should().Equal([1, 1]);
            (await File.ReadAllBytesAsync(second)).Should().Equal([2, 2, 2]);
            logs.Should().NotContain(line => line.Contains("分集计算通过", StringComparison.Ordinal));
            logs.Should().NotContain(line => line.Contains("集开始去重", StringComparison.Ordinal));
        }
        finally
        {
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public async Task Duration_merge_updates_declared_episode_count_and_reruns_later_steps()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "a.mp4"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(root, "b.mp4"), [2]);
        await File.WriteAllTextAsync(
            Path.Combine(root, ".weixin-channel-download-state.json"),
            """
            {
              "ok": true,
              "video_count": 2,
              "episode_number_mode": "source",
              "episode_mappings": [
                { "source_episode_number": 3, "sequence_episode_number": 1 },
                { "source_episode_number": 8, "sequence_episode_number": 2 }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "短剧信息.txt"),
            "新剧名: 测试\n集数: 2\n总集数: 2\n");
        await File.WriteAllTextAsync(
            Path.Combine(root, "shortdrama-project.json"),
            """
            {
              "episodeCount": 2,
              "effectiveEpisodeCount": 2,
              "downloadEpisodeLimit": 2,
              "originalEpisodeCount": 9,
              "truncatedForTikTokUpload": true
            }
            """);
        VideoDedupService.ProbeForTests = (_, _) =>
            Task.FromResult(new VideoDedupProbe(15, 1080, 1920, true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (args, _) =>
        {
            File.WriteAllBytes(args[^1], [8]);
            return Task.CompletedTask;
        };
        try
        {
            var preset = VideoDedupPreset.CreateDefault();
            preset.MinimumDurationSeconds = 20;
            preset.MaximumDurationSeconds = 40;
            var item = new QueueProjectItem
            {
                ProjectDir = root,
                EpisodeCount = 2,
                SourceEpisodeCount = 9,
                StatusText = QueueStepStatus.Completed,
            };
            item.StepStates[QueueStepKeys.RewriteInfo] = QueueStepStatus.Completed;
            item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Completed;
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1", VideoDedupPreset = preset };

            await VideoDedupService.RunAsync(item, account, force: false, _ => { }, CancellationToken.None);

            item.EpisodeCount.Should().Be(1);
            item.SourceEpisodeCount.Should().Be(9);
            item.StepStates[QueueStepKeys.RewriteInfo].Should().Be(QueueStepStatus.Pending);
            item.StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Pending);
            item.StatusText.Should().Be(QueueStepStatus.Pending);
            var state = await File.ReadAllTextAsync(Path.Combine(root, ".weixin-channel-download-state.json"));
            state.Should().Contain("\"video_count\": 1");
            state.Should().Contain("\"source_video_count\": 2");
            state.Should().Contain("\"episode_number_mode\": \"continuous\"");
            state.Should().NotContain("\"source_episode_number\": 8");
            var info = await File.ReadAllTextAsync(Path.Combine(root, "短剧信息.txt"));
            info.Should().Contain("集数: 1");
            info.Should().Contain("总集数: 1");
            var metadata = await File.ReadAllTextAsync(Path.Combine(root, "shortdrama-project.json"));
            metadata.Should().Contain("\"effectiveEpisodeCount\": 1");
            metadata.Should().Contain("\"downloadEpisodeLimit\": 1");
            metadata.Should().Contain("\"originalEpisodeCount\": 9");
        }
        finally
        {
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public async Task Failed_encode_leaves_the_downloaded_video_in_place()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "01.mp4");
        await File.WriteAllBytesAsync(source, [4, 5, 6]);
        var staging = Path.Combine(
            ProjectWorkspaceService.ResolveWorkflowProjectDir(root),
            TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(staging);
        var staleCopy = Path.Combine(staging, "old-第1集.mp4");
        await File.WriteAllBytesAsync(staleCopy, [7]);
        VideoDedupService.ProbeForTests = (_, _) =>
            Task.FromResult(new VideoDedupProbe(8, 720, 1280, true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (_, _) => throw new InvalidOperationException("encode failed");
        try
        {
            var item = new QueueProjectItem { ProjectDir = root, EpisodeCount = 1 };
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1" };
            var act = () => VideoDedupService.RunAsync(item, account, force: false, _ => { }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
            (await File.ReadAllBytesAsync(source)).Should().Equal([4, 5, 6]);
            (await File.ReadAllBytesAsync(staleCopy)).Should().Equal([7]);
            item.EpisodeCount.Should().Be(1);
        }
        finally
        {
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public async Task Interrupted_dedup_resumes_from_the_finished_episode()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "01.mp4"), [1, 1, 1]);
        await File.WriteAllBytesAsync(Path.Combine(root, "02.mp4"), [2, 2, 2]);
        VideoDedupService.ProbeForTests = (_, _) =>
            Task.FromResult(new VideoDedupProbe(8, 1080, 1920, true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (args, _) =>
        {
            if (Path.GetFileName(args[^1]).StartsWith("02", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("第二集中断");
            File.WriteAllBytes(args[^1], [9, 9, 9]);
            return Task.CompletedTask;
        };
        try
        {
            VideoDedupCpuBudget.ConcurrencyForTests = 1;
            var preset = VideoDedupPreset.CreateDefault();
            preset.Parallel = 1;
            var item = new QueueProjectItem { ProjectDir = root, EpisodeCount = 2 };
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1", VideoDedupPreset = preset };

            var interrupted = () => VideoDedupService.RunAsync(item, account, force: false, _ => { }, CancellationToken.None);
            await interrupted.Should().ThrowAsync<InvalidOperationException>();
            (await File.ReadAllBytesAsync(Path.Combine(root, "01.mp4"))).Should().Equal([1, 1, 1]);

            var encoded = 0;
            VideoDedupService.RunProcessForTests = (args, _) =>
            {
                encoded++;
                Path.GetFileName(args[^1]).Should().Be("02.mp4");
                File.WriteAllBytes(args[^1], [8, 8, 8]);
                return Task.CompletedTask;
            };
            var logs = new List<string>();
            await VideoDedupService.RunAsync(item, account, force: false, logs.Add, CancellationToken.None);

            encoded.Should().Be(1);
            logs.Should().Contain(line => line.Contains("第 1/2 集已完成，跳过编码", StringComparison.Ordinal));
            (await File.ReadAllBytesAsync(Path.Combine(root, "01.mp4"))).Should().Equal([9, 9, 9]);
            (await File.ReadAllBytesAsync(Path.Combine(root, "02.mp4"))).Should().Equal([8, 8, 8]);
            (await File.ReadAllBytesAsync(Path.Combine(root, "source-originals", "01.mp4"))).Should().Equal([1, 1, 1]);
        }
        finally
        {
            VideoDedupCpuBudget.ConcurrencyForTests = null;
            VideoDedupCpuBudget.Configure(VideoDedupCpuBudget.DefaultConcurrency, Environment.ProcessorCount);
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void Picture_filters_are_appended_after_geometry()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.Effects.Temperature = 40;
        preset.Sharpness = -1;
        preset.Effects.CurveAmount = 100;
        preset.Effects.CurveMaster =
        [
            new VideoDedupCurvePoint { X = 0, Y = 0 },
            new VideoDedupCurvePoint { X = 0.5, Y = 0.8 },
            new VideoDedupCurvePoint { X = 1, Y = 1 },
        ];
        preset.Effects.Layers.Add(new VideoDedupOverlayLayer
        {
            Kind = VideoDedupOverlayLayer.KindBorder,
            Color = "#FFFFFF",
            BorderPercent = 2,
        });
        preset.Normalize();

        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));

        joined.Should().Contain("setparams=range=tv:color_primaries=bt709");
        joined.Should().Contain("colorbalance=rm=0.2:bm=-0.2");
        joined.Should().Contain("gblur=sigma=1");
        joined.Should().NotContain("unsharp=");
        joined.Should().Contain("curves=");
        joined.Should().Contain("0.5/0.8");
        joined.Should().Contain("drawbox=");
        var geometryAt = joined.IndexOf("setparams=range=tv", StringComparison.Ordinal);
        var colorAt = joined.IndexOf("colorbalance=rm=0.2", StringComparison.Ordinal);
        colorAt.Should().BeGreaterThan(geometryAt);
    }

    [Fact]
    public void Mosaic_and_partial_lut_use_a_filter_graph()
    {
        var lut = Path.Combine(Path.GetTempPath(), "dedup-lut-" + Guid.NewGuid().ToString("N") + ".cube");
        File.WriteAllText(lut, "LUT_3D_SIZE 2");
        try
        {
            var preset = VideoDedupPreset.CreateDefault();
            preset.Effects.LutAmount = 50;
            preset.Effects.LutPath = lut;
            preset.Effects.MosaicAmount = 80;
            preset.Effects.MosaicRegions.Add(new VideoDedupMosaicRegion { X = 10, Y = 20, Width = 30, Height = 15, Block = 16 });
            preset.Normalize();

            var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, false));
            var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));
            joined.Should().Contain("-filter_complex");
            joined.Should().Contain("lut3d=");
            joined.Should().Contain("blend=all_opacity=0.5");
            joined.Should().Contain("crop=w=iw*0.3:h=ih*0.15");
        }
        finally
        {
            File.Delete(lut);
        }
    }

    [Fact]
    public void Missing_filter_asset_fails_before_encode()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.Effects.LutAmount = 100;
        preset.Effects.LutPath = Path.Combine(Path.GetTempPath(), "missing-dedup-lut.cube");
        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, false));

        var act = () => VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1);
        act.Should().Throw<InvalidOperationException>().WithMessage("*3D LUT*");
    }

    [Fact]
    public void Copying_a_preset_onto_accounts_leaves_their_other_settings_alone()
    {
        var source = VideoDedupPreset.CreateDefault();
        source.VideoBitrateMbps = 2;
        source.TrimStartSeconds = 2;
        var first = new TikTokAccountProfile { Id = "a", TiktokProxyHost = "10.0.0.1", VideoDedupPreset = VideoDedupPreset.CreateDefault() };
        var second = new TikTokAccountProfile { Id = "b", TiktokProxyHost = "10.0.0.2" };
        first.VideoDedupPreset!.VideoBitrateMbps = 4.2;

        source.CopyOnto([first, second]);

        first.TiktokProxyHost.Should().Be("10.0.0.1");
        second.TiktokProxyHost.Should().Be("10.0.0.2");
        first.VideoDedupPreset!.Fingerprint().Should().Be(source.Fingerprint());
        second.VideoDedupPreset!.Fingerprint().Should().Be(source.Fingerprint());
        first.VideoDedupPreset.VideoBitrateMbps = 8;
        second.VideoDedupPreset.VideoBitrateMbps.Should().Be(2);
        source.VideoBitrateMbps.Should().Be(2);
    }

    [Fact]
    public void Inactive_effects_do_not_change_the_dedup_fingerprint()
    {
        var untouched = VideoDedupPreset.CreateDefault().Fingerprint();
        var explicitDefaults = VideoDedupPreset.CreateDefault();
        explicitDefaults.Effects = new VideoDedupEffects();
        explicitDefaults.Fingerprint().Should().Be(untouched);

        var changed = VideoDedupPreset.CreateDefault();
        changed.Effects.Temperature = 12;
        changed.Fingerprint().Should().NotBe(untouched);
    }

    [Theory]
    [InlineData(0, 16, 2)]
    [InlineData(-3, 16, 2)]
    [InlineData(2, 16, 2)]
    [InlineData(8, 16, 8)]
    [InlineData(99, 16, 16)]
    [InlineData(4, 1, 1)]
    public void Concurrency_uses_the_configured_value_up_to_the_processor_count(
        int configured,
        int logicalProcessors,
        int expected) =>
        VideoDedupCpuBudget.Normalize(configured, logicalProcessors).Should().Be(expected);

    [Theory]
    [InlineData(16, 2, true)]
    [InlineData(16, 2, false)]
    [InlineData(16, 3, true)]
    [InlineData(8, 1, false)]
    public void Full_encode_load_splits_every_processor_across_the_configured_lanes(
        int logicalProcessors,
        int concurrency,
        bool hardware)
    {
        var lanes = VideoDedupCpuBudget.Normalize(concurrency, logicalProcessors);
        var total = 0;
        for (var lane = 0; lane < lanes; lane++)
        {
            var threads = VideoDedupCpuBudget.ThreadsFor(logicalProcessors, lanes, 100, lane, hardware);
            var perProcess = hardware
                ? threads.FilterThreads
                : threads.EncoderThreads + threads.FilterThreads;
            perProcess.Should().BeGreaterThan(0);
            if (hardware)
                threads.EncoderThreads.Should().Be(1);
            total += perProcess;
        }

        total.Should().Be(logicalProcessors);
    }

    [Theory]
    [InlineData(16, 90, 14)]
    [InlineData(8, 90, 7)]
    [InlineData(4, 90, 4)]
    [InlineData(32, 90, 29)]
    [InlineData(2, 90, 2)]
    [InlineData(16, 100, 16)]
    [InlineData(16, 50, 8)]
    [InlineData(16, 0, 14)]
    [InlineData(6, 49, 3)]
    public void Cpu_percent_uses_each_machines_processor_count(int logicalProcessors, int percent, int expected) =>
        VideoDedupCpuBudget.ProcessorBudget(logicalProcessors, percent).Should().Be(expected);

    [Fact]
    public void Cpu_percent_remainder_is_spread_across_lanes()
    {
        VideoDedupCpuBudget.ShareForLane(8, 2, 90, 0).Should().Be(4);
        VideoDedupCpuBudget.ShareForLane(8, 2, 90, 1).Should().Be(3);
        VideoDedupCpuBudget.ShareForLane(16, 2, 90, 0).Should().Be(7);
        VideoDedupCpuBudget.ShareForLane(16, 2, 90, 1).Should().Be(7);
        VideoDedupCpuBudget.ShareForLane(32, 4, 90, 0).Should().Be(8);
        VideoDedupCpuBudget.ShareForLane(32, 4, 90, 1).Should().Be(7);
        VideoDedupCpuBudget.ShareForLane(32, 4, 90, 3).Should().Be(7);
        VideoDedupCpuBudget.ShareForLane(4, 2, 90, 0).Should().Be(2);
        VideoDedupCpuBudget.ShareForLane(4, 2, 90, 1).Should().Be(2);

        var hardware = VideoDedupCpuBudget.ThreadsFor(16, 2, 90, 0, hardwareEncoder: true);
        hardware.EncoderThreads.Should().Be(1);
        hardware.FilterThreads.Should().Be(7);
        var software = VideoDedupCpuBudget.ThreadsFor(8, 2, 90, 1, hardwareEncoder: false);
        (software.EncoderThreads + software.FilterThreads).Should().Be(3);
    }

    [Fact]
    public async Task Encode_gate_admits_the_next_episode_when_concurrency_rises()
    {
        VideoDedupEncodeGate.Configure(1);
        try
        {
            await VideoDedupEncodeGate.WaitAsync(CancellationToken.None);
            var waiting = VideoDedupEncodeGate.WaitAsync(CancellationToken.None);
            waiting.IsCompleted.Should().BeFalse();
            VideoDedupEncodeGate.Configure(2);
            await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            VideoDedupEncodeGate.Release();
            VideoDedupEncodeGate.Release();
            VideoDedupEncodeGate.Configure(VideoDedupCpuBudget.DefaultConcurrency);
        }
    }

    [Fact]
    public async Task Cancelled_encode_wait_does_not_keep_a_slot()
    {
        VideoDedupEncodeGate.Configure(1);
        try
        {
            await VideoDedupEncodeGate.WaitAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource();
            var waiting = VideoDedupEncodeGate.WaitAsync(cts.Token);
            cts.Cancel();
            var act = async () => await waiting;
            await act.Should().ThrowAsync<OperationCanceledException>();
            VideoDedupEncodeGate.Release();

            var entered = VideoDedupEncodeGate.WaitAsync(CancellationToken.None);
            await entered.WaitAsync(TimeSpan.FromSeconds(2));
            VideoDedupEncodeGate.Release();
        }
        finally
        {
            VideoDedupEncodeGate.Release();
            VideoDedupEncodeGate.Configure(VideoDedupCpuBudget.DefaultConcurrency);
        }
    }

    [Fact]
    public void Encoder_arguments_limit_threads_for_the_current_machine()
    {
        var preset = VideoDedupPreset.CreateDefault().NormalizedCopy();
        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, true));
        var software = VideoDedupCpuBudget.ThreadsFor(Environment.ProcessorCount, hardwareEncoder: false);
        var cpu = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));
        cpu.Should().Contain($"-threads {software.EncoderThreads}");
        cpu.Should().Contain($"-filter_threads {software.FilterThreads}");
        cpu.Should().Contain($"-filter_complex_threads {software.FilterThreads}");
        cpu.Should().Contain($"threads={software.EncoderThreads}:lookahead-threads=1");

        var hardware = VideoDedupCpuBudget.ThreadsFor(Environment.ProcessorCount, hardwareEncoder: true);
        var amf = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "h264_amf", seed: 1));
        amf.Should().Contain($"-threads {hardware.EncoderThreads}");
        amf.Should().Contain($"-filter_threads {hardware.FilterThreads}");
        amf.Should().NotContain("x264-params");
    }

    [Fact]
    public void Auto_encoder_tries_the_detected_vendor_before_the_others()
    {
        VideoDedupHardwareOrder.Encoders(false, new VideoDedupHardwareOrder.Vendors(false, true, false), mac: false)
            .Should().ContainInOrder("h264_amf", "h264_nvenc", "h264_qsv");
        VideoDedupHardwareOrder.Encoders(false, new VideoDedupHardwareOrder.Vendors(true, true, true), mac: false)
            .Should().ContainInOrder("h264_nvenc", "h264_amf", "h264_qsv");
        VideoDedupHardwareOrder.Encoders(true, new VideoDedupHardwareOrder.Vendors(false, false, true), mac: true)
            .Should().ContainInOrder("hevc_qsv", "hevc_nvenc", "hevc_amf", "hevc_videotoolbox");
    }
}
