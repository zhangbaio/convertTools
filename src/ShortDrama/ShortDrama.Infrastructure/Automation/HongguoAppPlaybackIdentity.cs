namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果 APP 播放设备号和安装号。这一对来自匿名设备注册，不能本地随机拼接。
/// </summary>
public static class HongguoAppPlaybackIdentity
{
    public static bool IsComplete(string? deviceId, string? installId) =>
        HongguoGuestPlayback.IsSavedIdentity(deviceId, installId);

    public static async Task<(string DeviceId, string InstallId)> RegisterAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var playback = new HongguoGuestPlayback(httpClient);
        return await playback.SharedIdentityAsync(refresh: true, null, null, null, cancellationToken);
    }
}
