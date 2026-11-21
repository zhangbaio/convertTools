using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Weixin.Publishing;

internal interface IWeixinSeriesApiClient : IDisposable
{
    Task AuthenticateAsync(WeixinSeriesApiSession session, CancellationToken ct);
    Task CheckNameAsync(string title, CancellationToken ct);
    Task<string> UploadAsync(WeixinSeriesApiFile file, CancellationToken ct);
    Task CheckMediaAsync(JsonObject payload, CancellationToken ct);
    Task SubmitAsync(JsonObject payload, CancellationToken ct);
}

internal sealed class WeixinSeriesApiRejectedException(string message) : InvalidOperationException(message);
public sealed class WeixinSeriesApiQuotaExceededException(string message) : InvalidOperationException(message);

internal sealed partial class WeixinSeriesApiClient(HttpClient weixin, HttpClient cloud, string userAgent) : IWeixinSeriesApiClient
{
    private const string Channels = "https://channels.weixin.qq.com";
    private const string Mp = "https://mp.weixin.qq.com";
    private const string Vod = "https://vod2.qcloud.com/v3/index.php";
    private const string DefaultAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan VideoTransferTimeout = TimeSpan.FromHours(1);

    public static IWeixinSeriesApiClient Create(WeixinSeriesApiSession session, string userAgent) =>
        new WeixinSeriesApiClient(
            new HttpClient(new SocketsHttpHandler
            {
                CookieContainer = session.Cookies, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15),
            }) { Timeout = Timeout.InfiniteTimeSpan },
            new HttpClient(new SocketsHttpHandler
            {
                UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15),
            }) { Timeout = Timeout.InfiniteTimeSpan },
            userAgent);


    public async Task AuthenticateAsync(WeixinSeriesApiSession session, CancellationToken ct)
    {
        const string path = "/cgi-bin/mmfinderassistant-bin/svrkit/Mmfinderassistantlogin/genExternalCode";
        var query = $"?_aid={Escape(session.ActivityId)}&_rid={Guid.NewGuid():D}&_pageUrl={Escape(Channels + "/micro/content/playlet")}";
        using var request = WeixinRequest(HttpMethod.Post, Channels + path + query, JsonContent.Create(new
        {
            externalCodeScene = 10,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _log_finder_uin = session.FinderUin, _log_finder_id = session.FinderId, rawKeyBuff = session.FinderKey,
            pluginSessionId = (string?)null, scene = 7, reqScene = 7,
        }));
        var external = await RequireCodeAsync(weixin, request, "获取提审登录凭据", "errCode", ct).ConfigureAwait(false);
        var code = RequiredText(external.Get("data"), "externalCode", "视频号未返回提审凭据，请核对登录和剧集管理权限。");
        using var landing = new HttpRequestMessage(HttpMethod.Get, $"{Mp}/webpocnew/newAuditForm?code={Escape(code)}");
        using var response = await SendAsync(weixin, landing, "初始化提审会话", RequestTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(response, "初始化提审会话");
        using var login = WeixinPost("playletAudit/cpFinderLogin", new { code });
        await RequireCodeAsync(weixin, login, "登录提审服务", "base_resp.ret", ct).ConfigureAwait(false);
    }

    public async Task CheckNameAsync(string title, CancellationToken ct)
    {
        var result = await AuditAsync("checkDramaName", new { name = title }, "校验剧目名称", ct).ConfigureAwait(false);
        EnsureAuditMessage(result, "校验剧目名称");
    }

    public async Task CheckMediaAsync(JsonObject payload, CancellationToken ct)
    {
        var result = await AuditAsync("checkCreateDramaMedias",
            new { dramaName = payload["name"], mediaList = payload["mediaList"] }, "校验提审视频", ct).ConfigureAwait(false);
        EnsureAuditMessage(result, "校验提审视频");
        if (HasInvalidMedia(result.Get("invalidMediaList")))
            throw new WeixinSeriesApiRejectedException("视频号提示部分素材不可用于提审，请勾选「重新上传素材」刷新回执。");
    }

    public async Task SubmitAsync(JsonObject payload, CancellationToken ct) =>
        _ = await AuditAsync("createVodDrama", payload, "提交剧目审核", ct).ConfigureAwait(false);

    public Task<string> UploadAsync(WeixinSeriesApiFile file, CancellationToken ct) =>
        file.Episode.HasValue ? UploadEpisodeAsync(file, ct) : UploadMaterialAsync(file, ct);


    private async Task<string> UploadMaterialAsync(WeixinSeriesApiFile file, CancellationToken ct)
    {
        using var stream = OpenFile(file);
        using var content = new MultipartFormDataContent();
        var data = new StreamContent(stream);
        data.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(file.Path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png",
            ".webp" => "image/webp", ".pdf" => "application/pdf", ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        });
        content.Add(data, "file", file.Name);
        using var request = WeixinRequest(HttpMethod.Post, $"{Mp}/cgi-bin/filetransfer?action={file.Kind}&f=json", content);
        var result = await RequireCodeAsync(weixin, request, "上传剧集材料", "base_resp.ret", ct).ConfigureAwait(false);
        return RequiredText(result, "content", "材料上传响应中缺少材料编号。");
    }

    private async Task<string> UploadEpisodeAsync(WeixinSeriesApiFile file, CancellationToken ct)
    {
        using var signRequest = WeixinRequest(HttpMethod.Get, $"{Mp}/webpocnew/cgi/playletUpload/vodCliSdkGetSign?lang=zh_CN");
        var signatureResult = await RequireCodeAsync(weixin, signRequest, "获取分集上传凭据", "retInNode", ct).ConfigureAwait(false);
        var signature = RequiredText(signatureResult, "sign", "视频号未返回分集上传凭据。");
        var clientReportId = Guid.NewGuid().ToString("N");
        const string clientVersion = "1.3.1.0";
        var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var apply = new HttpRequestMessage(HttpMethod.Post, $"{Vod}?Action=ApplyUploadUGC")
        {
            Content = JsonContent.Create(new
            {
                signature, clientReportId, clientVersion, videoName = file.Name,
                videoType = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant(), videoSize = file.Length,
            }),
        };
        var upload = (await RequireCodeAsync(cloud, apply, "申请视频上传", "code", ct).ConfigureAwait(false)).Get("data");
        var vodSessionKey = RequiredText(upload, "vodSessionKey", "云存储未返回视频上传会话。");
        await UploadObjectAsync(file, upload, ct).ConfigureAwait(false);
        using var commit = new HttpRequestMessage(HttpMethod.Post, $"{Vod}?Action=CommitUploadUGC")
        {
            Content = JsonContent.Create(new { signature, clientReportId, clientVersion, vodSessionKey }),
        };
        var confirmed = (await RequireCodeAsync(cloud, commit, "确认视频上传", "code", ct).ConfigureAwait(false)).Get("data");
        var fileId = RequiredText(confirmed, "fileId", "云存储未返回视频编号。");
        using var register = WeixinPost("playletUpload/onVodCliSdkUploadFinished", new
        {
            fileId, uploadTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), uploadStartTime = start,
        });
        var receipt = await RequireCodeAsync(weixin, register, "登记视频号分集素材", "retInNode", ct).ConfigureAwait(false);
        return RequiredText(receipt.Get("mediaInfo"), "_id", "视频号未返回分集素材编号。");
    }

    private static FileStream OpenFile(WeixinSeriesApiFile file)
    {
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == file.Length && File.GetLastWriteTimeUtc(file.Path).Ticks == file.ModifiedUtcTicks)
            return stream;
        stream.Dispose();
        throw new InvalidOperationException($"上传文件已变化，请重新开始：{file.Name}");
    }

    public void Dispose()
    {
        weixin.Dispose();
        cloud.Dispose();
    }
}
