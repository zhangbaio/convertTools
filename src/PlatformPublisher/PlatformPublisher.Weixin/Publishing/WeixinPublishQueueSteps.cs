using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Weixin.Publishing;

public static class WeixinPublishQueueSteps
{
    private const string RetiredRemuxKey = "upload-remux";

    // Keep the retired step's saved record for history while excluding it from the working queue.
    public static IEnumerable<PublishJobStepState> ActiveStates(PublishJob job) =>
        job.Platform == PublishPlatform.WeixinChannel
            ? job.StepStates.Where(entry => !entry.Key.Equals(RetiredRemuxKey, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Value)
            : job.StepStates.Values;

    public static bool RecoverRetiredRemuxFailure(PublishJob job)
    {
        if (job.Platform != PublishPlatform.WeixinChannel ||
            job.Kind != PublishJobKind.Series ||
            job.Status != PublishJobStatus.Failed ||
            job.StepStates.GetValueOrDefault(RetiredRemuxKey) is not { Status: PublishJobStepStatus.Failed } remux ||
            !string.Equals(job.StatusMessage, remux.Message, StringComparison.Ordinal) ||
            ActiveStates(job).Any(step => step.Status is PublishJobStepStatus.Failed or PublishJobStepStatus.Running))
        {
            return false;
        }

        if (job.StepStates.GetValueOrDefault("weixin-upload") is { Status: PublishJobStepStatus.Succeeded } upload)
        {
            job.Status = PublishJobStatus.Succeeded;
            job.StatusMessage = "上传已完成";
            job.LastCompletedAt = upload.UpdatedAt;
        }
        else
        {
            job.Status = PublishJobStatus.Pending;
            job.StatusMessage = "等待执行";
        }

        // Reopening a previously failed job must not trigger an old scheduled upload.
        job.ScheduledAt = null;
        return true;
    }
}
