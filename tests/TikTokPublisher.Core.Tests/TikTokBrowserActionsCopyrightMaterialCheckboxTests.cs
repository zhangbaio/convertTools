using FluentAssertions;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokBrowserActionsCopyrightMaterialCheckboxTests
{
    [Fact]
    public void SelectedState_AcceptsNativeCheckbox()
    {
        TikTokBrowserActions.IsCopyrightMaterialCheckboxSelectedState(
                nativeChecked: true,
                ariaChecked: null,
                controlClass: null,
                wrapperClass: "semi-checkbox-wrapper",
                innerClass: "semi-checkbox-inner")
            .Should().BeTrue();
    }

    [Fact]
    public void SelectedState_AcceptsRoleCheckbox()
    {
        TikTokBrowserActions.IsCopyrightMaterialCheckboxSelectedState(
                nativeChecked: false,
                ariaChecked: "true",
                controlClass: null,
                wrapperClass: null,
                innerClass: null)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("semi-checkbox semi-checkbox-checked", null)]
    [InlineData("semi-checkbox-wrapper checked", null)]
    [InlineData("semi-checkbox-wrapper", "semi-checkbox-inner semi-checkbox-inner-checked")]
    public void SelectedState_AcceptsRenderedSemiCheckbox(
        string wrapperClass,
        string? innerClass)
    {
        TikTokBrowserActions.IsCopyrightMaterialCheckboxSelectedState(
                nativeChecked: false,
                ariaChecked: "false",
                controlClass: null,
                wrapperClass: wrapperClass,
                innerClass: innerClass)
            .Should().BeTrue();
    }

    [Fact]
    public void SelectedState_RejectsUncheckedCheckbox()
    {
        TikTokBrowserActions.IsCopyrightMaterialCheckboxSelectedState(
                nativeChecked: false,
                ariaChecked: "false",
                controlClass: "semi-checkbox-input",
                wrapperClass: "semi-checkbox semi-checkbox-unChecked semi-checkbox-cardType_enable",
                innerClass: "semi-checkbox-inner")
            .Should().BeFalse();
    }

    [Fact]
    public void UploadRetry_AllowsExactlyOneRetry()
    {
        TikTokBrowserActions.ShouldRetryCopyrightMaterialUpload(
                attempt: 1,
                new InvalidOperationException("上传失败"))
            .Should().BeTrue();
        TikTokBrowserActions.ShouldRetryCopyrightMaterialUpload(
                attempt: 2,
                new InvalidOperationException("再次失败"))
            .Should().BeFalse();
        TikTokBrowserActions.ShouldRetryCopyrightMaterialUpload(
                attempt: 1,
                new OperationCanceledException())
            .Should().BeFalse();
    }

    [Fact]
    public void UploadFailureMessage_IdentifiesFormAndSuggestsRetryLater()
    {
        var message = TikTokBrowserActions.BuildCopyrightMaterialUploadFailureMessage(
            "AI 生成过程截图",
            "检测到 1 个红色失败文件卡");

        message.Should().Contain("AI 生成过程截图");
        message.Should().Contain("文件上传失败");
        message.Should().Contain("TikTok 官方上传服务");
        message.Should().Contain("网络波动");
        message.Should().Contain("稍后重试");
    }

    [Fact]
    public void ManualUploadGuidance_ExplainsHowToDistinguishPlatformAndAutomationFailures()
    {
        var message = TikTokBrowserActions.BuildCopyrightMaterialManualUploadGuidance(
            "AI 生成过程截图",
            [@"E:\workflow\AI 生成过程截图\01.png", @"E:\workflow\AI 生成过程截图\02.png"],
            "检测到 2 个红色失败文件卡");

        message.Should().Contain("AI 生成过程截图");
        message.Should().Contain("自动上传两次均失败");
        message.Should().Contain("点击“+”手动上传");
        message.Should().Contain("01.png");
        message.Should().Contain(@"E:\workflow\AI 生成过程截图");
        message.Should().Contain("手动上传成功");
        message.Should().Contain("自动化");
        message.Should().Contain("手动上传也失败");
        message.Should().Contain("TikTok 官方上传服务");
        message.Should().Contain("稍后重试");
    }
}
