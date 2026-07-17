using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace ShortDrama.Desktop.Services;

public sealed class DesktopWeixinLoginNotificationService : IWeixinLoginNotificationService
{
    private static readonly TimeSpan NotifyCooldown = TimeSpan.FromMinutes(3);

    private readonly GlobalSettingsService _globalSettingsService;
    private readonly IFeishuNotificationService _feishuNotificationService;
    private readonly Dictionary<string, DateTimeOffset> _lastSentAtByFingerprint = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public DesktopWeixinLoginNotificationService(
        GlobalSettingsService globalSettingsService,
        IFeishuNotificationService feishuNotificationService)
    {
        _globalSettingsService = globalSettingsService;
        _feishuNotificationService = feishuNotificationService;
    }

    public async Task NotifyLoginRequiredAsync(
        WeixinLoginNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var global = _globalSettingsService.Load();
        if (!global.FeishuNotificationEnabled || !global.FeishuNotifyOnLoginQr)
        {
            return;
        }

        var feishuSettings = new FeishuNotificationSettings(
            Enabled: true,
            AppId: global.FeishuAppId,
            AppSecret: global.FeishuAppSecret,
            ReceiveId: global.FeishuReceiveId,
            ReceiveIdType: string.IsNullOrWhiteSpace(global.FeishuReceiveIdType) ? "chat_id" : global.FeishuReceiveIdType,
            NotifyOnStepStart: global.FeishuNotifyOnStepStart,
            NotifyOnStepSuccess: global.FeishuNotifyOnStepSuccess,
            NotifyOnStepFailure: global.FeishuNotifyOnStepFailure,
            NotifyOnQueueSummary: global.FeishuNotifyOnQueueSummary,
            StepKeysText: global.FeishuNotifyStepKeysText);

        var fingerprint = BuildFingerprint(request);
        lock (_gate)
        {
            ClearExpiredEntries();
            if (_lastSentAtByFingerprint.TryGetValue(fingerprint, out var lastSentAt) &&
                DateTimeOffset.UtcNow - lastSentAt < NotifyCooldown)
            {
                return;
            }

            _lastSentAtByFingerprint[fingerprint] = DateTimeOffset.UtcNow;
        }

        var title = "微信登录失效，请扫码";
        var lines = new List<string>
        {
            $"项目: {request.DisplayName}",
            $"设备: {Environment.MachineName}",
            $"目录: {request.ProjectDirectory}",
            $"后台: {request.BaseUrl}",
            $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            request.Message
        };

        if (!string.IsNullOrWhiteSpace(request.AuthFilePath))
        {
            lines.Add($"登录态文件: {request.AuthFilePath}");
        }

        await _feishuNotificationService.SendTextWithOptionalImageAsync(
            feishuSettings,
            title,
            string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line))),
            request.ScreenshotPath,
            cancellationToken);
    }

    private static string BuildFingerprint(WeixinLoginNotificationRequest request)
    {
        var screenshotKey = string.IsNullOrWhiteSpace(request.ScreenshotPath)
            ? "-"
            : Path.GetFullPath(request.ScreenshotPath);
        return string.Join(
            "|",
            request.ProjectKey,
            request.BaseUrl,
            request.AuthFilePath ?? string.Empty,
            screenshotKey);
    }

    private void ClearExpiredEntries()
    {
        var cutoff = DateTimeOffset.UtcNow - NotifyCooldown;
        var expiredKeys = _lastSentAtByFingerprint
            .Where(entry => entry.Value < cutoff)
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var key in expiredKeys)
        {
            _lastSentAtByFingerprint.Remove(key);
        }
    }
}
