using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokTodayUploadCountServiceTests
{
    [Fact]
    public void CountTodayUploads_isolated_by_selected_account()
    {
        var now = new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.FromHours(8));
        var items = new[]
        {
            Uploaded("账号1项目", "account-1", now.AddHours(-3)),
            Uploaded("账号2项目A", "account-2", now.AddHours(-2)),
            Uploaded("账号2项目B", "account-2", now.AddHours(-1)),
        };

        TikTokTodayUploadCountService.CountTodayUploads(
                items,
                "account-1",
                now: now,
                includeExecutionHistory: false)
            .Should().Be(1);
        TikTokTodayUploadCountService.CountTodayUploads(
                items,
                "account-2",
                now: now,
                includeExecutionHistory: false)
            .Should().Be(2);
    }

    [Fact]
    public void CountTodayUploads_does_not_include_other_days_for_selected_account()
    {
        var now = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.FromHours(8));
        var items = new[]
        {
            Uploaded("今日项目", "account-1", now.AddHours(-1)),
            Uploaded("昨日项目", "account-1", now.AddDays(-1)),
        };

        TikTokTodayUploadCountService.CountTodayUploads(
                items,
                "account-1",
                now: now,
                includeExecutionHistory: false)
            .Should().Be(1);
    }

    private static QueueProjectItem Uploaded(
        string title,
        string accountId,
        DateTimeOffset completedAt) => new()
    {
        ProjectDir = Path.Combine(Path.GetTempPath(), title),
        DisplayName = title,
        NewTitle = title,
        AccountProfileId = accountId,
        UploadCompletedAt = completedAt.ToString("O"),
    };
}
