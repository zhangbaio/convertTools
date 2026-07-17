using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IDramaDownloader
{
    Task<DramaDownloadResult> DownloadAsync(
        DramaDownloadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
