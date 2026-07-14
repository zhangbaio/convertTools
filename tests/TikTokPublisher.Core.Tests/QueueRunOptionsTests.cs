using FluentAssertions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueRunOptionsTests
{
    [Fact]
    public void FromDictionary_uses_default_steps_when_option_is_missing()
    {
        var options = QueueRunOptions.FromDictionary(new Dictionary<string, object?>());

        options.EnabledSteps.Should().Equal(QueueStepRegistry.UploadSeries);
    }

    [Fact]
    public void FromDictionary_preserves_empty_enabled_steps_when_option_exists()
    {
        var options = QueueRunOptions.FromDictionary(new Dictionary<string, object?>
        {
            ["enabled_steps"] = new List<object?>()
        });

        options.EnabledSteps.Should().BeEmpty();
    }

    [Fact]
    public void Project_image_generation_is_not_user_selectable_or_enabled_by_default()
    {
        QueueStepRegistry.UserSelectable
            .Select(step => step.Key)
            .Should().NotContain(QueueStepRegistry.GenerateProjectImages);
        QueueStepRegistry.DefaultEnabledSteps
            .Should().NotContain(QueueStepRegistry.GenerateProjectImages);
    }
}
