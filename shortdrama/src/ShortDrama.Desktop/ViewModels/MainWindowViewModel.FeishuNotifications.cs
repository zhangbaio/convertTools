using ShortDrama.Core.Models;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly string[] DefaultFeishuNotificationStepKeys =
    [
        "download",
        "transcode",
        "rewrite",
        "poster-rename",
        "project-image",
        "cost-report",
        "batch-file-rename",
        "material-convert",
        "weixin-upload",
        "weixin-material-upload"
    ];

    private async Task TryNotifyFeishuStepAsync(
        ProjectListItemViewModel project,
        string stepKey,
        string stepLabel,
        string phase,
        bool? ok,
        string? reason,
        CancellationToken cancellationToken)
    {
        var settings = BuildFeishuNotificationSettings();
        if (!ShouldNotifyFeishuStep(settings, stepKey, phase, ok))
        {
            return;
        }

        var title = "微信视频号步骤通知";
        var resultText = ok switch
        {
            true => "成功",
            false => "失败",
            _ => "开始"
        };
        var phaseText = string.Equals(phase, "before", StringComparison.OrdinalIgnoreCase) ? "执行前" : "执行后";
        var messageLines = new List<string>
        {
            $"项目: {project.DisplayName}",
            $"步骤: {stepLabel}",
            $"时机: {phaseText}",
            $"结果: {resultText}",
            $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        };

        if (!string.IsNullOrWhiteSpace(reason))
        {
            messageLines.Add($"说明: {reason!.Trim()}");
        }

        try
        {
            await _feishuNotificationService.SendTextAsync(
                settings,
                title,
                string.Join(Environment.NewLine, messageLines),
                cancellationToken);
            AppendLog(
                $"飞书通知已发送：{project.DisplayName} · {stepLabel} · {phaseText}",
                project.ProjectKey,
                project.DisplayName,
                stepKey,
                stepLabel);
        }
        catch (Exception ex)
        {
            AppendLog(
                $"飞书通知发送失败：{ex.Message}",
                project.ProjectKey,
                project.DisplayName,
                stepKey,
                stepLabel,
                isFailure: true);
        }
    }

    private async Task TryNotifyFeishuQueueSummaryAsync(
        IReadOnlyCollection<ProjectListItemViewModel> projects,
        string queueLabel,
        CancellationToken cancellationToken)
    {
        var settings = BuildFeishuNotificationSettings();
        if (!settings.Enabled || !settings.NotifyOnQueueSummary)
        {
            return;
        }

        var successCount = projects.Count(project => string.Equals(project.SchedulingStatus, "已完成", StringComparison.Ordinal));
        var failedProjects = projects
            .Where(project => string.Equals(project.SchedulingStatus, "失败", StringComparison.Ordinal))
            .ToArray();

        var lines = new List<string>
        {
            $"队列: {queueLabel}",
            $"项目总数: {projects.Count}",
            $"成功数量: {successCount}",
            $"失败数量: {failedProjects.Length}",
            $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        };

        if (failedProjects.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("失败项目:");
            foreach (var project in failedProjects.Take(10))
            {
                lines.Add($"- {project.DisplayName} / {project.FailedStep ?? project.CurrentStepLabel}");
            }
        }

        try
        {
            await _feishuNotificationService.SendTextAsync(
                settings,
                "微信视频号队列汇总",
                string.Join(Environment.NewLine, lines),
                cancellationToken);
            AppendLog("飞书队列汇总通知已发送。");
        }
        catch (Exception ex)
        {
            AppendLog($"飞书队列汇总通知发送失败：{ex.Message}", string.Empty, string.Empty, "feishu-summary", "飞书汇总", isFailure: true);
        }
    }

    private FeishuNotificationSettings BuildFeishuNotificationSettings()
    {
        var global = _configService.LoadGlobal();
        return new FeishuNotificationSettings(
            Enabled: global.FeishuNotificationEnabled,
            AppId: global.FeishuAppId,
            AppSecret: global.FeishuAppSecret,
            ReceiveId: global.FeishuReceiveId,
            ReceiveIdType: string.IsNullOrWhiteSpace(global.FeishuReceiveIdType) ? "chat_id" : global.FeishuReceiveIdType,
            NotifyOnStepStart: global.FeishuNotifyOnStepStart,
            NotifyOnStepSuccess: global.FeishuNotifyOnStepSuccess,
            NotifyOnStepFailure: global.FeishuNotifyOnStepFailure,
            NotifyOnQueueSummary: global.FeishuNotifyOnQueueSummary,
            StepKeysText: string.IsNullOrWhiteSpace(global.FeishuNotifyStepKeysText)
                ? string.Join(Environment.NewLine, DefaultFeishuNotificationStepKeys)
                : global.FeishuNotifyStepKeysText);
    }

    private static bool ShouldNotifyFeishuStep(
        FeishuNotificationSettings settings,
        string stepKey,
        string phase,
        bool? ok)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        var enabledSteps = ParseFeishuStepKeys(settings.StepKeysText);
        if (!enabledSteps.Contains(stepKey))
        {
            return false;
        }

        if (string.Equals(phase, "before", StringComparison.OrdinalIgnoreCase))
        {
            return settings.NotifyOnStepStart;
        }

        return ok switch
        {
            true => settings.NotifyOnStepSuccess,
            false => settings.NotifyOnStepFailure,
            _ => false
        };
    }

    private static HashSet<string> ParseFeishuStepKeys(string rawText)
    {
        var items = string.IsNullOrWhiteSpace(rawText)
            ? DefaultFeishuNotificationStepKeys
            : rawText.Replace(",", "\n", StringComparison.Ordinal)
                .Replace(";", "\n", StringComparison.Ordinal)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return items
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
