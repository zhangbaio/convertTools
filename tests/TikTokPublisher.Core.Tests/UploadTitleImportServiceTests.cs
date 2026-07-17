using FluentAssertions;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Tests;

public sealed class UploadTitleImportServiceTests
{
    [Fact]
    public void PickPreferredSearchMatch_Should_Report_Empty_Upstream_Result()
    {
        var (_, reason) = UploadTitleImportService.PickPreferredSearchMatch("亮亮就业", []);

        reason.Should().Be(UploadTitleImportService.EmptySearchResultReason);
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
}
