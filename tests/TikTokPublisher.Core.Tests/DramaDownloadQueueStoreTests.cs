using FluentAssertions;
using TikTokPublisher.Core.Drama;

namespace TikTokPublisher.Core.Tests;

public sealed class DramaDownloadQueueStoreTests
{
    [Fact]
    public void New_state_uses_system_default_author_exclude()
    {
        var state = new DramaDownloadQueueState();

        state.Version.Should().Be(DramaDownloadQueueState.CurrentVersion);
        state.AuthorExclude.Should().Be(DramaDownloadQueueState.DefaultAuthorExclude);
        state.AuthorExclude.Should().Contain("FlickReels");
        state.AuthorExclude.Should().Contain("ShortTV");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Normalize_migrates_affected_empty_author_exclude_to_system_default(int version)
    {
        var state = new DramaDownloadQueueState
        {
            Version = version,
            AuthorExclude = "",
        };

        DramaDownloadQueueStore.Normalize(state);

        state.Version.Should().Be(DramaDownloadQueueState.CurrentVersion);
        state.AuthorExclude.Should().Be(DramaDownloadQueueState.DefaultAuthorExclude);
    }

    [Fact]
    public void Normalize_keeps_version_4_custom_author_exclude()
    {
        var state = new DramaDownloadQueueState
        {
            Version = 4,
            AuthorExclude = "自定义作者",
        };

        DramaDownloadQueueStore.Normalize(state);

        state.Version.Should().Be(DramaDownloadQueueState.CurrentVersion);
        state.AuthorExclude.Should().Be("自定义作者");
    }

    [Fact]
    public void Normalize_keeps_explicit_empty_author_exclude_after_current_version()
    {
        var state = new DramaDownloadQueueState
        {
            Version = DramaDownloadQueueState.CurrentVersion,
            AuthorExclude = "",
        };

        DramaDownloadQueueStore.Normalize(state);

        state.AuthorExclude.Should().BeEmpty();
    }
}
