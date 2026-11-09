using FluentAssertions;
using Xunit;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;

namespace ShortDrama.Infrastructure.Tests.Automation;

public class DramaDownloadSourceSelectorTests
{
    [Theory]
    [InlineData("playlet", "hongguoapp:7701593495751904281", "playlet:7701593495751904281")]
    [InlineData("hongguoapp", "playlet:7701593495751904281", "hongguoapp:7701593495751904281")]
    [InlineData("hghigh", "hongguoapp:7701593495751904281", "hghigh:7701593495751904281")]
    [InlineData("playlet", "playlet:7701593495751904281", "playlet:7701593495751904281")]
    [InlineData("playlet", "mapleleaf:123", "mapleleaf:123")]
    [InlineData("pikachu", "7599558182226119705", "7599558182226119705")]
    [InlineData("hongguoapp", "playlet_ep:video-1", "playlet_ep:video-1")]
    public void Shared_series_ids_follow_the_current_chain(string chain, string bookId, string expected)
    {
        DramaDownloadSourceSelector.TryRetarget(bookId, chain).Should().Be(expected);
    }

    [Theory]
    [InlineData("dramabox", "hongguoapp:7701593495751904281")]
    [InlineData("pikachu", "hongguoapp:7701593495751904281")]
    [InlineData("playlet", "hongguoapp:playlet:7701")]
    public void Different_families_need_a_title_search(string chain, string bookId)
    {
        DramaDownloadSourceSelector.TryRetarget(bookId, chain).Should().BeNull();
    }

    [Fact]
    public void Remember_keeps_the_original_book_id_and_caches_each_chain()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"source-selector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "shortdrama-project.json");
        File.WriteAllText(path, """
            {
              "bookId": "hongguoapp:7701",
              "originalTitle": "破产之后见真心"
            }
            """);

        try
        {
            DramaDownloadSourceSelector.Remember(directory, "hongguoapp", "hongguoapp:7701", "破产之后见真心");
            DramaDownloadSourceSelector.Remember(directory, "playlet", "playlet:7701", "破产之后见真心");

            var json = File.ReadAllText(path);
            json.Should().Contain("\"bookId\": \"hongguoapp:7701\"");
            DramaDownloadSourceSelector.ReadCachedBookId(directory, "playlet").Should().Be("playlet:7701");
            DramaDownloadSourceSelector.ReadCachedBookId(directory, "hongguoapp").Should().Be("hongguoapp:7701");
            DramaDownloadSourceSelector.ReadEpisodeCount(directory).Should().Be(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Download_uses_the_current_chain_for_a_saved_hongguo_book_id()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"source-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "shortdrama-project.json"), """
            {
              "bookId": "hongguoapp:7701593495751904281",
              "originalTitle": "破产之后见真心",
              "episodeCount": 50
            }
            """);
        var progress = new RecordingProgress();
        var handler = new EmptyHandler();
        using var http = new HttpClient(handler);
        var router = new DramaSourceRouter(
            http,
            new FixedSettings(new DramaSourceSettings { DramaSourceChain = "playlet" }),
            new HongguoLocalApiService(http),
            new HongguoNewApiService(http),
            new HongguoDramaSearchService(http),
            new HongguoDramaDownloader(http),
            new HongguoMemoryReaderService());

        try
        {
            var result = await router.DownloadAsync(
                new DramaDownloadRequest(
                    ProjectDir: directory,
                    OutputDir: directory,
                    DisplayName: "破产试探见人心",
                    BookId: "hongguoapp:7701593495751904281",
                    Episodes: "25",
                    Quality: "720P",
                    Concurrent: 1,
                    MetadataDirectory: directory),
                progress,
                CancellationToken.None);

            result.Ok.Should().BeFalse();
            progress.Messages.Should().Contain(message => message.Contains("playlet:7701593495751904281", StringComparison.Ordinal));
            handler.Urls.Should().Contain(url => url.Contains("book_id=7701593495751904281", StringComparison.Ordinal));
            handler.Urls.Should().NotContain(url => url.Contains("video_model", StringComparison.OrdinalIgnoreCase));
            var json = File.ReadAllText(Path.Combine(directory, "shortdrama-project.json"));
            json.Should().Contain("\"bookId\": \"hongguoapp:7701593495751904281\"");
            DramaDownloadSourceSelector.ReadCachedBookId(directory, "playlet")
                .Should().Be("playlet:7701593495751904281");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedSettings(DramaSourceSettings settings) : IDramaSettingsProvider
    {
        public DramaSourceSettings Get() => settings;

        public void SavePikachuDeviceId(string deviceId)
        {
        }
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
