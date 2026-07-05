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

    [Fact]
    public void Normalize_migrates_legacy_empty_author_exclude_to_system_default()
    {
        var state = new DramaDownloadQueueState
        {
            Version = 3,
            AuthorExclude = "",
        };

        DramaDownloadQueueStore.Normalize(state);

        state.Version.Should().Be(DramaDownloadQueueState.CurrentVersion);
        state.AuthorExclude.Should().Be(DramaDownloadQueueState.DefaultAuthorExclude);
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
