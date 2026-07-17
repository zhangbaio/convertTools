namespace TikTokPublisher.Core.Queue;

/// <summary>队列步骤日志过滤：重复执行已完成步骤时只保留概要信息。</summary>
public static class QueueStepLogFilters
{
    /// <summary>
    /// 下载步骤已经在下载服务层过滤了百分比刷新，剩余消息均为逐集开始、转码、重试、完成等
    /// 生命周期日志，必须逐条传递，不能按“项目 + 步骤”覆盖或限频。
    /// </summary>
    public static bool RequiresLosslessUiDelivery(string? stepKey) =>
        string.Equals(stepKey, QueueStepRegistry.Download, StringComparison.Ordinal);

    public static Action<string> SummaryOnly(Action<string> forward) => message =>
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        if (IsSummaryMessage(message))
            forward(message);
    };

    private static bool IsSummaryMessage(string message)
    {
        if (message.Contains("正在等待视频文件上传完成", StringComparison.Ordinal) &&
            !message.Contains("视频已全部上传完成", StringComparison.Ordinal))
        {
            return false;
        }

        if (message.Contains("已删除源视频", StringComparison.Ordinal) ||
            message.Contains("本地 Paraformer", StringComparison.Ordinal) ||
            message.Contains("识别中", StringComparison.Ordinal))
        {
            return false;
        }

        if (message.Contains('⏳', StringComparison.Ordinal) ||
            message.Contains('⚠', StringComparison.Ordinal) ||
            message.Contains("失败", StringComparison.Ordinal) ||
            message.Contains("跳过", StringComparison.Ordinal))
        {
            return true;
        }

        if (message.StartsWith("开始", StringComparison.Ordinal) ||
            message.Contains("开始：", StringComparison.Ordinal) ||
            message.Contains("完成", StringComparison.Ordinal))
        {
            return true;
        }

        if (message.Contains("TikTok", StringComparison.Ordinal) &&
            (message.Contains("已", StringComparison.Ordinal) ||
             message.Contains("提交", StringComparison.Ordinal) ||
             message.Contains("保存", StringComparison.Ordinal)))
        {
            return true;
        }

        // 单集下载/识别明细（如 [12/43]）在重复执行时折叠掉。
        return message.Length <= 80 &&
               !message.Contains("[", StringComparison.Ordinal) &&
               !message.Contains("识别中", StringComparison.Ordinal) &&
               !message.Contains("ASR", StringComparison.Ordinal);
    }
}
