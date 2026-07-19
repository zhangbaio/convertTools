using System.Text.Json;
using AlibabaCloud.SDK.ICE20201109.Models;
using Aliyun.OSS;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

internal sealed class AliyunImsSubtitleClient
{
    private readonly ClientSettings _settings;
    private readonly AlibabaCloud.SDK.ICE20201109.Client _ims;
    private readonly OssClient _oss;

    public AliyunImsSubtitleClient(ClientSettings settings)
    {
        _settings = settings;
        _ims = new AlibabaCloud.SDK.ICE20201109.Client(new AlibabaCloud.OpenApiClient.Models.Config
        {
            AccessKeyId = settings.AliyunSubtitleAccessKeyId,
            AccessKeySecret = settings.AliyunSubtitleAccessKeySecret,
            RegionId = settings.AliyunSubtitleRegion,
            Endpoint = $"ice.{settings.AliyunSubtitleRegion}.aliyuncs.com",
        });
        _oss = new OssClient(OssEndpoint(settings.AliyunSubtitleRegion),
            settings.AliyunSubtitleAccessKeyId, settings.AliyunSubtitleAccessKeySecret);
    }

    public async Task UploadAsync(string filePath, string objectKey, Action<double>? progress, CancellationToken ct)
    {
        progress?.Invoke(0);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await Task.Run(() => _oss.PutObject(_settings.AliyunSubtitleOssBucket, objectKey, stream), ct)
            .ConfigureAwait(false);
        progress?.Invoke(1);
    }

    public async Task<string> StartSubtitleRemovalAsync(string inputObjectKey, string outputObjectKey,
        CancellationToken ct)
    {
        var request = new SubmitIProductionJobRequest
        {
            Name = $"TikTok去字幕-{Path.GetFileNameWithoutExtension(inputObjectKey)}",
            FunctionName = "VideoDetext",
            Input = new()
            {
                Type = "OSS",
                Media = OssUri(inputObjectKey),
            },
            Output = new()
            {
                Type = "OSS",
                Media = OssUri(outputObjectKey),
            },
            ModelId = _settings.AliyunSubtitleAdvancedModel ? "algo-video-detext-new" : null,
            JobParams = BuildJobParams(_settings.AliyunSubtitleLimitRegion),
        };
        var response = await _ims.SubmitIProductionJobAsync(request).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return response.Body?.JobId ?? throw new InvalidOperationException("阿里云 IMS 未返回去字幕任务 ID。");
    }

    public async Task<AliyunSubtitleResult> GetSubtitleRemovalAsync(string taskId, CancellationToken ct)
    {
        var response = await _ims.QueryIProductionJobAsync(new QueryIProductionJobRequest { JobId = taskId })
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var body = response.Body;
        var outputUrl = body?.OutputUrls?.FirstOrDefault() ?? body?.Output?.OutputUrl ?? "";
        return new AliyunSubtitleResult(body?.Status ?? "Unknown", outputUrl, body?.Result ?? "");
    }

    public Uri CreateDownloadUri(string objectKey) =>
        _oss.GeneratePresignedUri(_settings.AliyunSubtitleOssBucket, objectKey,
            DateTime.Now.AddHours(2), SignHttpMethod.Get);

    internal static string? BuildJobParams(string? limitRegion)
    {
        if (string.IsNullOrWhiteSpace(limitRegion)) return null;
        try
        {
            using var document = JsonDocument.Parse(limitRegion);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException();
            return JsonSerializer.Serialize(new { LimitRegion = document.RootElement });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "阿里云字幕检测区域格式无效，应为归一化坐标数组，例如 [[0,0.65,1,0.35]]。");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "阿里云字幕检测区域格式无效，应为归一化坐标数组，例如 [[0,0.65,1,0.35]]。");
        }
    }

    private string OssUri(string objectKey) => $"oss://{_settings.AliyunSubtitleOssBucket}/{objectKey}";

    private static string OssEndpoint(string region) => $"https://oss-{region}.aliyuncs.com";
}

internal sealed record AliyunSubtitleResult(string Status, string OutputUrl, string Error);
