using FluentAssertions;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class VisionAndBrowserFallbackTests
{
    [Fact]
    public void Vision_json_keeps_the_first_object_and_drops_trailing_text()
    {
        var json = """{"shot1":{"shot_type":"近景"},"shot2":{"shot_type":"中景"}} 后面的说明}""";

        var extracted = TikTokAiGenerationScreenshotService.ExtractJsonObject(json);

        extracted.Should().Be("""{"shot1":{"shot_type":"近景"},"shot2":{"shot_type":"中景"}}""");
    }

    [Fact]
    public void Vision_json_ignores_braces_inside_strings()
    {
        var json = """{"note":"a } b","ok":true} 多余""";

        TikTokAiGenerationScreenshotService.ExtractJsonObject(json).Should().Be("""{"note":"a } b","ok":true}""");
    }

    [Fact]
    public void Recovered_vision_log_stays_informational()
    {
        LogMessageLevelClassifier.InferLevel(
            "AI 截图/反推 [1/4] 镜头 1-2：返回内容不是 JSON，已改用本地描述。")
            .Should().Be("info");
        LogMessageLevelClassifier.InferLevel(
            "AI 截图/反推：8 个镜头，6 个来自模型，2 个使用本地描述。")
            .Should().Be("info");
    }

    [Fact]
    public void Playwright_finds_the_current_and_legacy_browser_layouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "playwright-layout-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "chromium-1208", "chrome-win64", "chrome.exe");
        var currentHeadless = Path.Combine(root, "chromium_headless_shell-1208", "chrome-headless-shell-win64", "chrome-headless-shell.exe");
        var legacy = Path.Combine(root, "chromium-1169", "chrome-win", "chrome.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        Directory.CreateDirectory(Path.GetDirectoryName(currentHeadless)!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllBytes(current, [1]);
        File.WriteAllBytes(currentHeadless, [1]);
        File.WriteAllBytes(legacy, [1]);
        try
        {
            PlaywrightBrowserRuntime.FindWindowsExecutableForTests(root, preferHeadlessShell: false)
                .Should().Be(current);
            PlaywrightBrowserRuntime.FindWindowsExecutableForTests(root, preferHeadlessShell: true)
                .Should().Be(currentHeadless);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
