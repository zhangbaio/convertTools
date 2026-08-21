using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services;

public static partial class LogMessageLevelClassifier
{
    public static string InferLevel(string? text)
    {
        var message = text ?? "";
        var errorScanMessage = ZeroFailureCountRegex().Replace(message, "");
        if (ContainsAny(
                errorScanMessage,
                "失败", "错误", "异常", "无法", "终止", "崩溃", "未成功", "未通过",
                "failed", "failure", "error", "exception", "invalid"))
        {
            return "error";
        }

        var warningScanMessage = SafeDuplicateHandlingRegex().Replace(message, "");
        warningScanMessage = RetryConfigurationRegex().Replace(warningScanMessage, "");
        warningScanMessage = TimeoutConfigurationRegex().Replace(warningScanMessage, "");
        if (ContainsAny(
                warningScanMessage,
                "警告", "重试", "兜底", "重复", "相似", "不合格", "未发现", "未找到", "缺少",
                "超时", "warn", "warning", "retry", "timeout"))
        {
            return "warn";
        }

        if (ContainsAny(
                message,
                "成功", "完成", "已完成", "通过", "已保存", "已生成", "已同步", "已绑定",
                "downloaded", "uploaded", "succeeded", "success", "done"))
        {
            return "success";
        }

        return "info";
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(
        @"(?ix)
          (?:
              失败
            | failed
            | failures?
          )
          \s* [：:=]? \s* 0
          (?= \s* (?:$|[，,。；;、）)]) )")]
    private static partial Regex ZeroFailureCountRegex();

    [GeneratedRegex(@"(?:正在|开始|继续|已)?(?:排除|避免|防止|去除)重复(?:人物|角色|文件|项目|记录)?")]
    private static partial Regex SafeDuplicateHandlingRegex();

    [GeneratedRegex(@"(?i)(?:单集)?重试(?:次数|上限|配置)\s*[：:=]?\s*\d+")]
    private static partial Regex RetryConfigurationRegex();

    [GeneratedRegex(@"(?i)(?:单集)?超时(?:时间|配置)?\s*[：:=]?\s*\d+(?:\.\d+)?\s*(?:ms|s|秒|分钟)?")]
    private static partial Regex TimeoutConfigurationRegex();
}
