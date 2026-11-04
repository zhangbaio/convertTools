using FluentAssertions;
using TikTokPublisher.Core.Remote;

namespace TikTokPublisher.Core.Tests;

public sealed class DispatchConfirmSessionTests
{
    [Fact]
    public void Appending_does_not_reset_the_countdown()
    {
        var start = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new DispatchConfirmCoordinator();
        var first = coordinator.Offer(Item(1, "格佳信息", "personal", "剧A"), start, out var session);
        var second = coordinator.Offer(Item(2, "薄荷剧场", "enterprise", "剧B"), start.AddSeconds(20), out _);

        first.Should().Be(DispatchConfirmOffer.Opened);
        second.Should().Be(DispatchConfirmOffer.Appended);
        session!.Deadline.Should().Be(start.AddSeconds(60));
        session.Countdown.Should().Be(TimeSpan.FromSeconds(60));
        session.RemainingSeconds(start.AddSeconds(20)).Should().Be(40);
        session.BodyText().Should().Contain("共 2 个账号，2 部剧");
        session.BodyText().Should().Contain("格佳信息 · 个人版（1 部）");
        session.BodyText().Should().Contain("薄荷剧场 · 企业版（1 部）");
    }

    [Fact]
    public void Configured_countdown_stays_with_the_first_command()
    {
        var start = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new DispatchConfirmCoordinator();
        coordinator.Offer(Item(1, "格佳信息", "personal", "剧A", 30), start, out var session);
        coordinator.Offer(Item(2, "薄荷剧场", "enterprise", "剧B", 120), start.AddSeconds(10), out _);

        session!.Deadline.Should().Be(start.AddSeconds(30));
        session.RemainingSeconds(start.AddSeconds(10)).Should().Be(20);
        DispatchConfirmSession.NormalizeCountdownSeconds(0).Should().Be(60);
        DispatchConfirmSession.NormalizeCountdownSeconds(3).Should().Be(60);
        DispatchConfirmSession.NormalizeCountdownSeconds(90).Should().Be(90);
    }

    [Fact]
    public void Reject_once_opens_a_new_prompt_for_the_next_command()
    {
        var start = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new DispatchConfirmCoordinator();
        coordinator.Offer(Item(1, "格佳信息", "personal", "剧A"), start, out var first);
        first!.Decision.TrySetResult(DispatchConfirmDecision.RejectOnce);
        coordinator.TryConsume(out var decision, out var items).Should().BeTrue();
        decision.Should().Be(DispatchConfirmDecision.RejectOnce);
        items.Should().ContainSingle(item => item.MessageId == 1);

        var later = start.AddMinutes(2);
        var again = coordinator.Offer(Item(9, "格佳信息", "personal", "剧C"), later, out var second);
        again.Should().Be(DispatchConfirmOffer.Opened);
        second!.Deadline.Should().Be(later.AddSeconds(60));
        second.Snapshot().Should().ContainSingle(item => item.Title == "剧C");
    }

    [Fact]
    public void Timeout_executes_the_batch_that_arrived_during_the_minute()
    {
        var start = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new DispatchConfirmCoordinator();
        coordinator.Offer(Item(1, "格佳信息", "", "剧A"), start, out _);
        coordinator.Offer(Item(1, "格佳信息", "", "剧A"), start.AddSeconds(5), out var session);
        coordinator.Offer(Item(2, "格佳信息", "", "剧B"), start.AddSeconds(5), out _);
        coordinator.ExpireIfDue(start.AddSeconds(59)).Should().BeFalse();
        coordinator.ExpireIfDue(start.AddSeconds(60)).Should().BeTrue();
        coordinator.TryConsume(out var decision, out var items).Should().BeTrue();
        decision.Should().Be(DispatchConfirmDecision.Execute);
        items.Select(item => item.MessageId).Should().Equal(1, 2);
        session!.BodyText().Should().Contain("共 1 个账号，2 部剧");
        session.BodyText().Should().NotContain("个人版");
    }

    private static DispatchConfirmItem Item(long id, string account, string edition, string title, int confirmSeconds = 0) => new()
    {
        MessageId = id,
        AssignmentId = id,
        AccountName = account,
        Edition = edition,
        Title = title,
        ConfirmSeconds = confirmSeconds,
    };
}
