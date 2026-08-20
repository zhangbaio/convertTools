using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokReferenceSourcePackageServiceTests
{
    [Fact]
    public void Character_profiles_are_extracted_from_reference_style_script()
    {
        const string script = """
                              人物设定
                              陆小满（女主）：23岁，清秀温婉，身穿素白连衣裙。
                              陆景琛（男主）：27岁，清冷沉稳的集团掌权人。
                              赵母（反派）：市井势利妇人，尖酸刻薄。

                              第一幕
                              陆小满：今天是我的订婚宴。
                              """;

        var profiles = TikTokReferenceSourcePackageService.ExtractCharacterProfiles(script);

        profiles.Select(x => x.Name).Should().Equal("陆小满", "陆景琛", "赵母");
        profiles.Should().OnlyContain(x => x.Description.Contains(x.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Character_prompt_requires_photorealistic_single_person_without_text()
    {
        var prompt = TikTokReferenceSourcePackageService.BuildCharacterPrompt(
            new TikTokReferenceSourcePackageService.CharacterProfile(
                "陆小满",
                "23岁中国女性，清秀温婉，身穿素白连衣裙。"));

        prompt.Should().Contain("真实真人影视剧演员定妆摄影");
        prompt.Should().Contain("画面中仅一人");
        prompt.Should().Contain("无文字、无Logo、无水印");
        prompt.Should().Contain("不是插画");
    }

    [Fact]
    public void Character_profiles_fall_back_to_explicit_name_list_in_synopsis()
    {
        const string intro = "民间组织成员莫老头、邱胖子、沈秋三人率队携石像生赴滇西村落执行秘密任务。";

        var extracted = TikTokReferenceSourcePackageService.ExtractCharacterProfiles("", intro);

        // The public extractor deliberately returns only direct script matches. The package
        // generator subsequently enriches an undersized result from the synopsis list.
        extracted.Should().BeEmpty();
        var profiles = TikTokReferenceSourcePackageService.AddFallbackCharacters(extracted, intro);
        profiles.Take(3).Select(x => x.Name).Should().Equal("莫老头", "邱胖子", "沈秋");
    }

    [Fact]
    public void Explorer_capture_uses_reference_package_directories_when_package_is_current()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"reference-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var root = TikTokReferenceSourcePackageService.GetRoot(workflow);
            var character = Path.Combine(root, TikTokReferenceSourcePackageService.CharacterDirectoryName);
            var material = Path.Combine(root, TikTokReferenceSourcePackageService.MaterialDirectoryName, "001");
            var info = Path.Combine(root, "测试短剧");
            Directory.CreateDirectory(character);
            Directory.CreateDirectory(material);
            Directory.CreateDirectory(info);
            for (var index = 0; index < 3; index++)
                SaveSolidImage(Path.Combine(character, $"角色{index + 1}.png"));
            SaveSolidImage(Path.Combine(root, TikTokReferenceSourcePackageService.CharacterWorkbenchFileName));
            SaveSolidImage(Path.Combine(root, TikTokReferenceSourcePackageService.SceneDesignFileName1));
            SaveSolidImage(Path.Combine(root, TikTokReferenceSourcePackageService.SceneDesignFileName2));
            File.WriteAllText(
                Path.Combine(
                    TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflow),
                    TikTokReferenceSourcePackageService.StateFileName),
                "{}");
            File.WriteAllText(Path.Combine(material, "001-1.mp4.索引.txt"), "D:\\video.mp4");
            File.WriteAllText(Path.Combine(info, "shortdrama-project.json"), "{}");

            var outputs = Enumerable.Range(1, 4)
                .Select(index => Path.Combine(workflow, $"{index}.png"))
                .ToArray();
            var requests = TikTokSourceFileInfoScreenshotService.BuildExplorerCaptureRequests(workflow, outputs);

            requests.Select(x => x.Directory).Should().Equal(root, character, material, info);
            requests.Select(x => x.LargeIcons).Should().Equal(false, true, true, false);
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    private static void SaveSolidImage(string path)
    {
        using var image = new Image<Rgba32>(64, 64, new Rgba32(80, 100, 120));
        image.SaveAsPng(path);
    }
}
