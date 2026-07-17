using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IVideoMaterialConverter
{
    Task<VideoMaterialConvertResult> ConvertAsync(
        VideoMaterialConvertRequest request,
        IProgress<VideoMaterialConvertProgress>? progress,
        CancellationToken cancellationToken);
}
