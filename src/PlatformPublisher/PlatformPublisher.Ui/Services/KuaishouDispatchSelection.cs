using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Desktop.Services;

public static class KuaishouDispatchSelection
{
    /// <summary>
    /// 分发新剧时，今日加入的剧和已经执行过的剧继续勾选。
    /// 更早加入、还没执行的手动勾选可以清掉。
    /// </summary>
    public static bool ShouldStayChecked(PublishJob job, bool alreadyChecked, DateTimeOffset now)
    {
        if (WasAddedToday(job, now) || WasExecutedToday(job, now))
            return true;
        return alreadyChecked && WasExecuted(job);
    }

    public static bool WasAddedToday(PublishJob job, DateTimeOffset now) =>
        job.CreatedAt.ToLocalTime().Date == now.ToLocalTime().Date;

    public static bool WasExecutedToday(PublishJob job, DateTimeOffset now)
    {
        if (job.Status == PublishJobStatus.Running)
            return true;
        var today = now.ToLocalTime().Date;
        if (job.LastStartedAt?.ToLocalTime().Date == today || job.LastCompletedAt?.ToLocalTime().Date == today)
            return true;
        return job.StepStates.Values.Any(step =>
            step.Status != PublishJobStepStatus.Pending &&
            step.UpdatedAt.ToLocalTime().Date == today);
    }

    public static bool WasExecuted(PublishJob job) =>
        job.Status is PublishJobStatus.Running or PublishJobStatus.Succeeded or PublishJobStatus.Failed or PublishJobStatus.Blocked
        || job.LastStartedAt is not null
        || job.LastCompletedAt is not null
        || job.AttemptCount > 0
        || job.StepStates.Values.Any(step => step.Status != PublishJobStepStatus.Pending);
}
