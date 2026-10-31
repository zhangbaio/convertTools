using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouPageNoticeTests
{
    [Fact]
    public void Takedown_notice_with_countdown_close_button_is_recognized()
    {
        const string dialog = "短剧下架通知\n短剧她藏身份逆转危局由于平台风控限制，已被下架处理，不可上架。\n关闭 (13s)\n查看详情";

        Assert.True(KuaishouPageNotice.IsBlockingNotice(dialog));
        Assert.True(KuaishouPageNotice.IsNoticeCloseLabel("关闭 (13s)"));
        Assert.False(KuaishouPageNotice.IsNoticeCloseLabel("查看详情"));
    }

    [Fact]
    public void Form_dialogs_are_not_closed_as_notices()
    {
        Assert.False(KuaishouPageNotice.IsBlockingNotice("添加单集信息\n请输入集数"));
        Assert.False(KuaishouPageNotice.IsBlockingNotice("图片裁剪\n确定"));
    }
}
