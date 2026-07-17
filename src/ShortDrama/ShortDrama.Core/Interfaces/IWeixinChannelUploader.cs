using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWeixinChannelUploader
{
    Task<WeixinUploadResult> UploadAsync(
        WeixinUploadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
