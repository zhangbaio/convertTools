using FluentAssertions;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

// 取自 TikTok 编辑页改版为卡片列表后的真实 DOM（2026-10-30 失败快照）。
public sealed class TikTokEpisodeCardListTests
{
    [Fact]
    public void VideoInput_PrefersContinueUploadInputOverEpisodeCardInputs()
    {
        var cardInput = TikTokBrowserActions.ScoreVideoFileInputCandidate(
            ".mp4,.mov", multiple: false, "semi-upload-hidden-input", insideEpisodeCard: true);
        var continueInput = TikTokBrowserActions.ScoreVideoFileInputCandidate(
            ".mp4,.mov", multiple: true, "semi-upload-hidden-input", insideEpisodeCard: false);
        var replaceInput = TikTokBrowserActions.ScoreVideoFileInputCandidate(
            ".mp4,.mov", multiple: false, "semi-upload-hidden-input-replace", insideEpisodeCard: false);
        var coverInput = TikTokBrowserActions.ScoreVideoFileInputCandidate(
            "image/*", multiple: false, "semi-upload-hidden-input", insideEpisodeCard: false);

        cardInput.Should().BeNull("卡片内的上传框只替换该集视频");
        coverInput.Should().BeNull();
        continueInput.Should().BeGreaterThan(replaceInput!.Value);
    }

    [Fact]
    public void CardListText_DetectsMisalignmentAfterFirstEpisode()
    {
        const string cardText =
            """
            第 1 集 替嫁农女携夫共赴锦绣人生-第1集
            2026-10-30 09:27
            草稿
            第 2 集 替嫁农女携夫共赴锦绣人生-第12集
            第 3 集 替嫁农女携夫共赴锦绣人生-第4集
            第 4 集 替嫁农女携夫共赴锦绣人生-第4集
            第 5 集 替嫁农女携夫共赴锦绣人生-第2集
            """;

        var rows = TikTokBrowserActions.ParseEditVideoRowsFromText(cardText);

        rows.Select(row => (row.Slot, row.Real)).Should().Equal((1, 1), (2, 12), (3, 4), (4, 4), (5, 2));
        TikTokBrowserActions.FindAlignedEditVideoPrefixCount(rows).Should().Be(1);
    }

    [Fact]
    public void SeriesListRow_TranslatesUntranslatedStatusKeys()
    {
        TikTokSeriesListLookupService.TranslateUntranslatedStatusKeys(
                "冰海争鲜兄弟分途 ID: 7702261516644455444 contentPartnerHub_selfUpload_statusChecking 详情")
            .Should().Be("冰海争鲜兄弟分途 ID: 7702261516644455444 检测中 详情");
        TikTokSeriesListLookupService.TranslateUntranslatedStatusKeys(
                "某剧 contentPartnerHub_seriesStatus_copyrightSuspected contentPartnerHub_seriesStatus_unknownThing")
            .Should().Be("某剧 疑似版权问题 contentPartnerHub_seriesStatus_unknownThing");
        TikTokSeriesListLookupService.TranslateUntranslatedStatusKeys("我的人偶都是鉴宝大师 草稿")
            .Should().Be("我的人偶都是鉴宝大师 草稿");
    }

    [Fact]
    public void ClickedDeleteResult_ReadsSlotCardUidAndText()
    {
        TikTokBrowserActions.TryReadClickedEditVideoRow(
                "clicked:12\tep-7701\t第 12 集 剧名-第4集",
                out var slot,
                out var uid,
                out var text)
            .Should().BeTrue();

        slot.Should().Be(12);
        uid.Should().Be("ep-7701");
        text.Should().Be("第 12 集 剧名-第4集");
    }

    [Fact]
    public void ClickedDeleteResult_TableRowHasNoCardUid()
    {
        TikTokBrowserActions.TryReadClickedEditVideoRow("clicked:3\t\t第 3 集", out var slot, out var uid, out _)
            .Should().BeTrue();

        slot.Should().Be(3);
        uid.Should().BeEmpty();
        TikTokBrowserActions.TryReadClickedEditVideoRow("not-rendered:1,2", out _, out _, out _)
            .Should().BeFalse();
    }
}
