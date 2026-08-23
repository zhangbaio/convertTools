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
}
