using FluentAssertions;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Tests;

public sealed class UploadTitleImportServiceTests
{
    [Fact]
    public void StrictDispatchMatchRequiresUniqueOriginalTitleAndEpisodeTotal()
    {
        var first = new DramaSearchItem { BookId = "hongguoapp:1", Title = "The Child’s Story", EpisodeTotal = 52 };
        var otherVersion = new DramaSearchItem { BookId = "hongguoapp:2", Title = first.Title, EpisodeTotal = first.EpisodeTotal };

        Assert.Same(first, UploadTitleImportService.PickStrictDispatchMatch(" the  child's story ", 52, [first]).Item);
        Assert.Null(UploadTitleImportService.PickStrictDispatchMatch("The Child's Story 2", 52, [first]).Item);
        Assert.Null(UploadTitleImportService.PickStrictDispatchMatch("The Child's Story", 60, [first]).Item);
        Assert.Null(UploadTitleImportService.PickStrictDispatchMatch("The Child's Story", 52, [first, otherVersion]).Item);
        Assert.Null(UploadTitleImportService.PickStrictDispatchMatch("The Child's Story", 0, [first]).Item);
    }


    [Fact]
    public void DispatchedBookUsesTheSearchHitThatCarriesTheSynopsis()
    {
        var dispatched = new DramaSearchItem
        {
            BookId = "dramabox:9001",
            Title = "The Dragon King's Fated Wet Nurse",
            EpisodeTotal = 97,
            Intro = "A wet nurse raises the dragon king.",
            PosterUrl = "https://cdn.example/poster.jpg",
        };
        var sameTitle = new DramaSearchItem
        {
            BookId = "dramabox:9002",
            Title = dispatched.Title,
            EpisodeTotal = 40,
            Intro = "Another synopsis",
        };

        var (item, reason) = UploadTitleImportService.PickDispatchedBook(
            "dramabox:9001",
            [sameTitle, dispatched]);

        reason.Should().BeEmpty();
        item.Should().BeSameAs(dispatched);
        item!.Intro.Should().Be(dispatched.Intro);
    }

    [Fact]
    public void DispatchedBookMissesWhenTheSearchPageDoesNotContainThatId()
    {
        var other = new DramaSearchItem { BookId = "dramabox:1", Title = "Other", Intro = "Synopsis" };

        var (missing, missingReason) = UploadTitleImportService.PickDispatchedBook("dramabox:9001", [other]);
        var (empty, emptyReason) = UploadTitleImportService.PickDispatchedBook("dramabox:9001", []);

        missing.Should().BeNull();
        missingReason.Should().Be(UploadTitleImportService.NoExactMatchReason);
        empty.Should().BeNull();
        emptyReason.Should().Be(UploadTitleImportService.EmptySearchResultReason);
    }

    [Fact]
    public void BookIdForSeries_maps_hongguo_app_and_offline_to_the_same_chain()
    {
        UploadTitleImportService.BookIdForSeries("hongguoapp", "731").Should().Be("hongguoapp:731");
        UploadTitleImportService.BookIdForSeries("hgoffline", "731").Should().Be("hongguoapp:731");
        UploadTitleImportService.BookIdForSeries("unknown", "731").Should().Be("");
    }

    [Fact]
    public void PickPreferredSearchMatch_Should_Report_Empty_Upstream_Result()
    {
        var (_, reason) = UploadTitleImportService.PickPreferredSearchMatch("亮亮就业", []);

        reason.Should().Be(UploadTitleImportService.EmptySearchResultReason);
    }

    [Fact]
    public void Truncated_keep_count_accepts_the_single_longer_same_title()
    {
        var shorter = new DramaSearchItem { Title = "穗金是锦鲤第四季", EpisodeTotal = 80, BookId = "a" };
        var longer = new DramaSearchItem { Title = "穗金是锦鲤第四季", EpisodeTotal = 139, BookId = "b" };

        var (item, reason) = UploadTitleImportService.PickPreferredSearchMatch(
            "穗金是锦鲤第四季",
            [shorter, longer],
            expectedEpisodeTotal: 120,
            truncatedKeepCount: 120);

        reason.Should().BeEmpty();
        item!.BookId.Should().Be("b");
    }

    [Fact]
    public void Truncated_keep_count_rejects_multiple_longer_same_titles()
    {
        var first = new DramaSearchItem { Title = "穗金是锦鲤第四季", EpisodeTotal = 139, BookId = "a" };
        var second = new DramaSearchItem { Title = "穗金是锦鲤第四季", EpisodeTotal = 150, BookId = "b" };

        var (item, reason) = UploadTitleImportService.PickPreferredSearchMatch(
            "穗金是锦鲤第四季",
            [first, second],
            expectedEpisodeTotal: 120,
            truncatedKeepCount: 120);

        item.Should().BeNull();
        reason.Should().Contain("无法唯一确认");
    }

    [Fact]
    public void Exact_episode_total_still_wins_over_a_longer_candidate()
    {
        var exact = new DramaSearchItem { Title = "穗金是锦鲤第四季", EpisodeTotal = 120, BookId = "exact" };
        var longer = new DramaSearchItem { Title = "穗金是锦鲤第四季", EpisodeTotal = 139, BookId = "long" };

        var (item, reason) = UploadTitleImportService.PickPreferredSearchMatch(
            "穗金是锦鲤第四季",
            [exact, longer],
            expectedEpisodeTotal: 120,
            truncatedKeepCount: 120);

        reason.Should().BeEmpty();
        item!.BookId.Should().Be("exact");
    }

    [Fact]
    public void ResolveEpisodeLimitError_blocks_over_limit_when_global_switch_is_off()
    {
        var item = new DramaSearchItem
        {
            Title = "超长短剧",
            EpisodeTotal = 150,
        };
        var settings = new ClientSettings
        {
            TiktokAllowOverLimitUploadImport = false,
            TiktokOverLimitDownloadEpisodeCount = 120,
        };

        var error = UploadTitleImportService.ResolveEpisodeLimitError(item, settings);

        error.Should().Contain("大于最大限制 120");
    }

    [Fact]
    public void ResolveDownloadPlan_truncates_over_limit_items_when_global_switch_is_on()
    {
        var item = new DramaSearchItem
        {
            Title = "超长短剧",
            EpisodeTotal = 150,
        };
        var settings = new ClientSettings
        {
            TiktokAllowOverLimitUploadImport = true,
            TiktokOverLimitDownloadEpisodeCount = 120,
        };

        var error = UploadTitleImportService.ResolveEpisodeLimitError(item, settings);
        var plan = UploadTitleImportService.ResolveDownloadPlan(item, settings);

        error.Should().BeEmpty();
        plan.Truncated.Should().BeTrue();
        plan.Episodes.Should().Be("1-120");
        plan.EffectiveEpisodeCount.Should().Be(120);
    }

    [Fact]
    public void ResolveDownloadPlan_truncates_to_configured_keep_count()
    {
        var item = new DramaSearchItem
        {
            Title = "超长短剧",
            EpisodeTotal = 100,
        };
        var settings = new ClientSettings
        {
            TiktokAllowOverLimitUploadImport = true,
            TiktokMaximumEpisodeCount = 80,
            TiktokOverLimitDownloadEpisodeCount = 80,
        };

        var plan = UploadTitleImportService.ResolveDownloadPlan(item, settings);

        plan.Truncated.Should().BeTrue();
        plan.Episodes.Should().Be("1-80");
        plan.EffectiveEpisodeCount.Should().Be(80);
    }

    [Fact]
    public void BuildFailurePreview_prefers_author_excluded_failures()
    {
        var failures = new[]
        {
            new UploadTitleImportFailure("剧A", "未找到精确匹配结果"),
            new UploadTitleImportFailure("剧B", $"{UploadTitleImportService.AuthorExcludedFailurePrefix}河马剧场（包含 河马）"),
        };

        var preview = UploadTitleImportService.BuildFailurePreview(failures);

        preview.Should().Be("剧B: 命中作者排除：河马剧场（包含 河马）");
    }

    [Fact]
    public void BuildAuthorExcludeNotice_includes_filtered_reason()
    {
        var failures = new[]
        {
            new UploadTitleImportFailure("剧A", $"{UploadTitleImportService.AuthorExcludedFailurePrefix}掌玩短剧（包含 掌玩）"),
            new UploadTitleImportFailure("剧B", "集数 8，小于最小限制 10"),
        };

        var notice = UploadTitleImportService.BuildAuthorExcludeNotice(failures);

        notice.Should().Be("作者排除原因：剧A: 命中作者排除：掌玩短剧（包含 掌玩）。");
    }

    [Theory]
    [InlineData("dramabox", false)]
    [InlineData("DramaBox", false)]
    [InlineData("dramabox:42000030939", false)]
    [InlineData(" playlet ", true)]
    [InlineData("hghigh:123", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void DramaBox_does_not_apply_author_exclusion(string? chain, bool expected)
    {
        UploadTitleImportService.ShouldApplyAuthorExclusion(chain).Should().Be(expected);
    }

    [Fact]
    public async Task Unknown_author_requires_explicit_confirmation_when_exclusions_are_enabled()
    {
        var item = new DramaSearchItem
        {
            BookId = "hghigh:123",
            Title = "待确认短剧",
            EpisodeTotal = 72,
            Author = "",
        };

        var allowed = await UploadTitleImportService.ConfirmUnknownAuthorIfNeededAsync(
            item,
            item.Title,
            ["河马"],
            confirmUnknownAuthor: null,
            log: null,
            CancellationToken.None);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_author_can_continue_after_user_confirmation()
    {
        var item = new DramaSearchItem
        {
            BookId = "hghigh:123",
            Title = "待确认短剧",
            EpisodeTotal = 72,
            Author = "",
        };
        UnknownAuthorConfirmationRequest? captured = null;

        var allowed = await UploadTitleImportService.ConfirmUnknownAuthorIfNeededAsync(
            item,
            item.Title,
            ["河马"],
            (request, _) =>
            {
                captured = request;
                return Task.FromResult(true);
            },
            log: null,
            CancellationToken.None);

        allowed.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.BookId.Should().Be("hghigh:123");
    }

    [Fact]
    public async Task Known_author_does_not_request_secondary_confirmation()
    {
        var item = new DramaSearchItem
        {
            BookId = "hghigh:123",
            Title = "已知作者短剧",
            Author = "普通剧场",
        };
        var callbackCalled = false;

        var allowed = await UploadTitleImportService.ConfirmUnknownAuthorIfNeededAsync(
            item,
            item.Title,
            ["河马"],
            (_, _) =>
            {
                callbackCalled = true;
                return Task.FromResult(false);
            },
            log: null,
            CancellationToken.None);

        allowed.Should().BeTrue();
        callbackCalled.Should().BeFalse();
    }
}
