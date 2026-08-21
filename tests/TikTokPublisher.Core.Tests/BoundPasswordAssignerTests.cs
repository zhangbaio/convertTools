using FluentAssertions;
using TikTokPublisher.Core.Drama;

namespace TikTokPublisher.Core.Tests;

public sealed class BoundPasswordAssignerTests
{
    [Fact]
    public void Empty_Current_Kicks_Then_Assigns_Target()
    {
        BoundPasswordAssigner.AssignmentSteps("", "abc")
            .Should().Equal(BoundPasswordAssigner.KickValue, "abc");
    }

    [Fact]
    public void Same_NonEmpty_Clears_Then_Reassigns()
    {
        BoundPasswordAssigner.AssignmentSteps("abc", "abc")
            .Should().Equal("", "abc");
    }

    [Fact]
    public void Different_NonEmpty_Assigns_Directly()
    {
        BoundPasswordAssigner.AssignmentSteps("old", "new")
            .Should().Equal("new");
    }
}
