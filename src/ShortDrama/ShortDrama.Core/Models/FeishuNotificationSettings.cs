namespace ShortDrama.Core.Models;

public sealed record FeishuNotificationSettings(
    bool Enabled,
    string AppId,
    string AppSecret,
    string ReceiveId,
    string ReceiveIdType,
    bool NotifyOnStepStart,
    bool NotifyOnStepSuccess,
    bool NotifyOnStepFailure,
    bool NotifyOnQueueSummary,
    string StepKeysText,
    string ApiBase = "https://open.feishu.cn");
