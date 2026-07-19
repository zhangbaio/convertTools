using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokSpeechRateRepairServiceTests
{
    [Fact]
    public void BuildIntervals_only_marks_fast_english_cues_and_caps_stretch()
    {
        var cues = new[]
        {
            new TikTokSpeechRateRepairService.Cue(TimeSpan.Zero, TimeSpan.FromSeconds(2), "We will be there soon"),
            new TikTokSpeechRateRepairService.Cue(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "This sentence has far too many words to fit naturally"),
        };

        var result = TikTokSpeechRateRepairService.BuildIntervals(cues, 3.2, 3.6, 1.2);

        result.Should().ContainSingle();
        result[0].Start.Should().Be(3);
        result[0].Factor.Should().Be(1.2);
    }

    [Fact]
    public void Map_shifts_following_timestamps_after_stretched_interval()
    {
        var intervals = new[] { new TikTokSpeechRateRepairService.RepairInterval(2, 4, 1.2) };

        TikTokSpeechRateRepairService.Map(3, intervals).Should().BeApproximately(3.2, 0.0001);
        TikTokSpeechRateRepairService.Map(5, intervals).Should().BeApproximately(5.4, 0.0001);
    }
}
