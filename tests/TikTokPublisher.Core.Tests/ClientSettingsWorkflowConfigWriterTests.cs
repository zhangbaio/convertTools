using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ClientSettingsWorkflowConfigWriterTests
{
    [Fact]
    public async Task WriteTempConfig_creates_unique_files_without_cross_account_overwrites()
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 64)
            .Select(index => Task.Run(() =>
            {
                gate.Wait();
                var settings = new ClientSettings
                {
                    AiTextModel = $"model-{index}",
                    PosterTitleVerifyAiRetryCount = index % 4,
                    FrameExtractEpisodeIndex = index + 1,
                    FrameExtractTime = index + 1.5,
                    FrameExtractNeighborOffsetsSeconds = $"{index + 1},{index + 2}",
                    FrameExtractFallbackPercents = $"{index + 10},{index + 20}",
                };
                var account = new TikTokAccountProfile
                {
                    Id = $"account-{index}",
                    TiktokAiRewriteSynopsis = index % 2 == 0,
                };
                var path = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings, account);
                return (Index: index, Path: path);
            }))
            .ToArray();

        gate.Set();
        var results = await Task.WhenAll(tasks);

        try
        {
            results.Select(result => result.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Should().HaveCount(results.Length);

            foreach (var result in results)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(result.Path));
                document.RootElement.GetProperty("AiTextModel").GetString().Should().Be($"model-{result.Index}");
                document.RootElement.GetProperty("AiRewriteSynopsis").GetBoolean().Should().Be(result.Index % 2 == 0);
                document.RootElement.GetProperty("PosterTitleVerifyAiRetryCount").GetInt32()
                    .Should().Be(result.Index % 4);
                document.RootElement.GetProperty("FrameExtractEpisodeIndex").GetInt32().Should().Be(result.Index + 1);
                document.RootElement.GetProperty("FrameExtractTime").GetDouble().Should().Be(result.Index + 1.5);
                document.RootElement.GetProperty("FrameExtractNeighborOffsetsSeconds").GetString()
                    .Should().Be($"{result.Index + 1},{result.Index + 2}");
                document.RootElement.GetProperty("FrameExtractFallbackPercents").GetString()
                    .Should().Be($"{result.Index + 10},{result.Index + 20}");
            }
        }
        finally
        {
            foreach (var result in results)
            {
                try { File.Delete(result.Path); }
                catch { /* best-effort test cleanup */ }
            }
        }
    }
}
