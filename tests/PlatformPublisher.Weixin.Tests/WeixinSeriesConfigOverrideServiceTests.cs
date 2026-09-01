using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Weixin.Publishing;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinSeriesConfigOverrideServiceTests
{
    [Fact]
    public void PrepareSelectsEpisodesInjectsAiAndPreservesSourceConfig()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "weixin-series-override-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(tempRoot, "project");
        var dataRoot = Path.Combine(tempRoot, "data");
        Directory.CreateDirectory(Path.Combine(projectRoot, "videos"));
        for (var index = 1; index <= 4; index++)
            File.WriteAllBytes(Path.Combine(projectRoot, "videos", $"episode-{index}.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(projectRoot, "poster.png"), [1]);

        var sourcePath = Path.Combine(projectRoot, "weixin-channel-submit.json");
        var sourceJson = """
            {
              "auth_file": "auth.json",
              "debug": { "save_html": true, "save_text": true },
              "first_page": {
                "actions": [
                  { "type": "upload", "paths": ["poster.png"] }
                ]
              },
              "second_page": {
                "upload": {
                  "paths": [
                    "videos/episode-1.mp4",
                    "videos/episode-2.mp4",
                    "videos/episode-3.mp4",
                    "videos/episode-4.mp4"
                  ]
                }
              },
              "video_publish": { "enabled": true }
            }
            """;
        File.WriteAllText(sourcePath, sourceJson);

        try
        {
            var options = new WeixinPublishOptions
            {
                EpisodeSelectionMode = "explicit",
                EpisodeIndexes = "2,4",
                CaptureDebugDumps = false,
                AiDescriptionEnabled = true,
            };
            var service = new WeixinSeriesConfigOverrideService(dataRoot, new FakeAiProvider());
            var plan = service.Prepare(new PublishJob
            {
                Id = "series-job",
                Kind = PublishJobKind.Series,
                ProjectDirectory = projectRoot,
                ConfigPath = sourcePath,
                PublishCount = 4,
                PlatformOptionsJson = options.ToJson(),
            });

            Assert.NotNull(plan);
            Assert.Equal(4, plan!.OriginalVideoCount);
            Assert.Equal(2, plan.SelectedVideoCount);
            Assert.Equal(sourceJson, File.ReadAllText(sourcePath));
            Assert.StartsWith(Path.GetFullPath(dataRoot), Path.GetFullPath(plan.OverrideConfigPath), StringComparison.OrdinalIgnoreCase);

            using var document = JsonDocument.Parse(File.ReadAllText(plan.OverrideConfigPath));
            var root = document.RootElement;
            var paths = root.GetProperty("second_page").GetProperty("upload").GetProperty("paths");
            Assert.Equal(2, paths.GetArrayLength());
            Assert.EndsWith("episode-2.mp4", paths[0].GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("episode-4.mp4", paths[1].GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(Path.IsPathRooted(root.GetProperty("first_page").GetProperty("actions")[0].GetProperty("paths")[0].GetString()));
            Assert.False(root.GetProperty("debug").GetProperty("save_html").GetBoolean());
            Assert.Equal("https://ai.example/v1", root.GetProperty("video_publish").GetProperty("ai_text_endpoint").GetString());
            Assert.Equal("local", root.GetProperty("video_publish").GetProperty("ai_description_asr_engine").GetString());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class FakeAiProvider : IAiRuntimeSettingsProvider
    {
        public AiRuntimeSettings Load() => new(
            "https://ai.example/v1",
            "secret",
            "model-1",
            90,
            @"D:\models\asr",
            @"D:\models\vad.onnx",
            "app-id",
            "token",
            "zh-CN");
    }
}
