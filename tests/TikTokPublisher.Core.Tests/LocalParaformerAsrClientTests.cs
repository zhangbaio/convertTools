using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.Asr;

namespace TikTokPublisher.Core.Tests;

public sealed class LocalParaformerAsrClientTests
{
    [Fact]
    public void CreateTranscriptSegment_preserves_trimmed_text_and_sample_timing()
    {
        var segment = LocalParaformerAsrClient.CreateTranscriptSegment(
            startSample: 24_000,
            sampleCount: 12_000,
            sampleRate: 16_000,
            recognizedText: "  这是第一句台词。  ");

        segment.Should().NotBeNull();
        segment!.Value.StartSeconds.Should().BeApproximately(1.5, 0.000_001);
        segment.Value.EndSeconds.Should().BeApproximately(2.25, 0.000_001);
        segment.Value.Text.Should().Be("这是第一句台词。");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("……！")]
    [InlineData("啊呀嗯")]
    public void CreateTranscriptSegment_discards_non_lexical_results(string? text)
    {
        var segment = LocalParaformerAsrClient.CreateTranscriptSegment(0, 16_000, 16_000, text);

        segment.Should().BeNull();
    }

    [Fact]
    public void ToSpeechIntervals_maps_transcript_timing_without_changing_order()
    {
        IReadOnlyList<TranscriptSegment> segments =
        [
            new TranscriptSegment(3.25, 4.5, "第二句"),
            new TranscriptSegment(0.5, 1.75, "第一句"),
        ];

        var intervals = LocalParaformerAsrClient.ToSpeechIntervals(segments);

        intervals.Should().Equal(
            new SpeechInterval(3.25, 4.5),
            new SpeechInterval(0.5, 1.75));
    }

    [Fact]
    public async Task RecognizeLocalTranscriptAsync_honors_pre_cancelled_token_before_model_or_ffmpeg()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await TikTokSilenceAsrService.RecognizeLocalTranscriptAsync(
            "does-not-exist.mp4",
            new ClientSettings(),
            log: null,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
