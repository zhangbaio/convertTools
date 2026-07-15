using FluentAssertions;
using ShortDrama.Infrastructure.Files;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterLayoutDetectionPolicyTests
{
    [Theory]
    [InlineData(null, 0.7f, 0.7f, 0.1f)]
    [InlineData(0.1f, null, 0.7f, 0.1f)]
    [InlineData(0.1f, 0.7f, null, 0.1f)]
    [InlineData(0.1f, 0.7f, 0.7f, null)]
    public void TryValidateCoordinates_Should_Reject_Missing_Required_Field(
        float? x,
        float? y,
        float? width,
        float? height)
    {
        PosterLayoutDetectionPolicy.TryValidateCoordinates(x, y, width, height, out var reason)
            .Should().BeFalse();
        reason.Should().Contain("不能为 null");
    }

    [Theory]
    [InlineData(0.2f, 0.7f, 0.6f, 0.15f)]
    [InlineData(0f, 0f, 1f, 1f)]
    public void TryValidateCoordinates_Should_Accept_Reasonable_Region(
        float x,
        float y,
        float width,
        float height)
    {
        PosterLayoutDetectionPolicy.TryValidateCoordinates(x, y, width, height, out var reason)
            .Should().BeTrue();
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-0.1f, 0.7f, 0.6f, 0.1f)]
    [InlineData(0.9f, 0.7f, 0.2f, 0.1f)]
    [InlineData(0.1f, 0.95f, 0.6f, 0.1f)]
    [InlineData(0.1f, 0.7f, 0f, 0.1f)]
    public void TryValidateCoordinates_Should_Reject_Out_Of_Bounds_Or_Empty_Region(
        float x,
        float y,
        float width,
        float height)
    {
        PosterLayoutDetectionPolicy.TryValidateCoordinates(x, y, width, height, out _)
            .Should().BeFalse();
    }
}
