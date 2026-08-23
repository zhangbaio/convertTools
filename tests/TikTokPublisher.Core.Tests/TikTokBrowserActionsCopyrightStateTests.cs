using FluentAssertions;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokBrowserActionsCopyrightStateTests
{
    [Fact]
    public void SelectedState_AcceptsNativeInputState()
    {
        TikTokBrowserActions.IsCopyrightRadioSelectedState(
                inputChecked: true,
                inputAriaChecked: null,
                roleAriaChecked: null,
                labelClass: "semi-radio",
                innerClass: "semi-radio-inner")
            .Should().BeTrue();
    }

    [Fact]
    public void SelectedState_AcceptsSemiLabelState_WhenNativeInputIsNotSynchronized()
    {
        TikTokBrowserActions.IsCopyrightRadioSelectedState(
                inputChecked: false,
                inputAriaChecked: null,
                roleAriaChecked: null,
                labelClass: "semi-radio semi-radio-checked",
                innerClass: "semi-radio-inner")
            .Should().BeTrue();
    }

    [Fact]
    public void SelectedState_AcceptsSemiInnerAndAriaStates()
    {
        TikTokBrowserActions.IsCopyrightRadioSelectedState(
                inputChecked: false,
                inputAriaChecked: null,
                roleAriaChecked: null,
                labelClass: "semi-radio",
                innerClass: "semi-radio-inner semi-radio-inner-checked")
            .Should().BeTrue();

        TikTokBrowserActions.IsCopyrightRadioSelectedState(
                inputChecked: false,
                inputAriaChecked: null,
                roleAriaChecked: "true",
                labelClass: null,
                innerClass: null)
            .Should().BeTrue();
    }

    [Fact]
    public void SelectedState_RejectsUncheckedState()
    {
        TikTokBrowserActions.IsCopyrightRadioSelectedState(
                inputChecked: false,
                inputAriaChecked: "false",
                roleAriaChecked: "false",
                labelClass: "semi-radio",
                innerClass: "semi-radio-inner")
            .Should().BeFalse();
    }
}
