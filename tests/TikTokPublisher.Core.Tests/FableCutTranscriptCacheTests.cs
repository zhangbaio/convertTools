using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.ProjectImages.FableCut;

namespace TikTokPublisher.Core.Tests;

public sealed class FableCutTranscriptCacheTests
{
    [Fact]
    public void Settings_fingerprint_changes_when_resolved_asr_model_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fablecut-asr-fingerprint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var model = Path.Combine(root, "model.int8.onnx");
            var tokens = Path.Combine(root, "tokens.txt");
            var vad = Path.Combine(root, "silero_vad.onnx");
            File.WriteAllBytes(model, [1, 2, 3]);
            File.WriteAllText(tokens, "token");
            File.WriteAllBytes(vad, [4, 5, 6]);
            var settings = new ClientSettings
            {
                TiktokAsrLocalModelDir = root,
                TiktokAsrLocalVadPath = vad,
            };

            var before = FableCutTranscriptCache.ComputeSettingsFingerprint(settings);
            File.AppendAllText(model, "changed");
            var after = FableCutTranscriptCache.ComputeSettingsFingerprint(settings);

            after.Should().NotBe(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
