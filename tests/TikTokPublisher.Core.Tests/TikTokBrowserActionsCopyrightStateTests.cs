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

    [Theory]
    [InlineData(true, false, null, "trigger-iF4aJp", true)]
    [InlineData(true, false, "false", "trigger-iF4aJp semi-dropdown-showing", true)]
    [InlineData(true, false, null, "trigger-iF4aJp triggerCascadeLocked-dG71jy", false)]
    [InlineData(true, false, null, "trigger-iF4aJp triggerCascadeLocked-newHash", false)]
    [InlineData(true, true, null, "trigger-iF4aJp", false)]
    [InlineData(true, false, "true", "trigger-iF4aJp", false)]
    [InlineData(false, false, null, "trigger-iF4aJp", false)]
    public void MaterialTriggerState_RequiresUnlockedCascade(
        bool connected,
        bool disabled,
        string? ariaDisabled,
        string className,
        bool expected)
    {
        TikTokBrowserActions.IsCopyrightMaterialTriggerUnlockedState(
                connected,
                disabled,
                ariaDisabled,
                className)
            .Should().Be(expected);
    }
}
