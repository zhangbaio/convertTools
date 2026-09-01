using System.Globalization;
using PlatformPublisher.Core.Models;

namespace PlatformPublisher.Core.Services;

public static class PublishSchedulePolicy
{
    public static bool IsDue(PublishJob job, DateTimeOffset now) =>
        job.Status == PublishJobStatus.Pending &&
        job.ScheduledAt is { } scheduledAt &&
        scheduledAt <= now;

    public static bool CanRunNow(PublishJob job, DateTimeOffset now) =>
        job.Status is PublishJobStatus.Pending or PublishJobStatus.Failed &&
        (job.ScheduledAt is null || job.ScheduledAt <= now);

    public static int RecoverInterrupted(IEnumerable<PublishJob> jobs)
    {
        var recovered = 0;
        foreach (var job in jobs.Where(job => job.Status == PublishJobStatus.Running))
        {
            job.Status = PublishJobStatus.Pending;
            job.StatusMessage = "检测到上次执行中断，已恢复为待执行";
            job.UpdatedAt = DateTimeOffset.Now;
            recovered++;
        }

        return recovered;
    }

    public static bool TryParseLocal(string text, out DateTimeOffset value)
    {
        if (DateTime.TryParseExact(
                text?.Trim(),
                ["yyyy-MM-dd HH:mm", "yyyy/M/d H:mm", "yyyy-MM-dd H:mm"],
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var local))
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
            return true;
        }

        value = default;
        return false;
    }
}
