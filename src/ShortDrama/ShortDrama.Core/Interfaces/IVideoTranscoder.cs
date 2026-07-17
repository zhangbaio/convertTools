using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IVideoTranscoder
{
    Task<VideoTranscodeResult> TranscodeAsync(
        VideoTranscodeRequest request,
        IProgress<VideoTranscodeProgress>? progress,
        CancellationToken cancellationToken);
}
