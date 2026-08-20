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

    [Fact]
    public void Role_vector_renderer_replaces_only_declared_template_slots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"role-vector-template-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var characters = new[]
            {
                Path.Combine(root, "角色1.png"),
                Path.Combine(root, "角色2.png"),
                Path.Combine(root, "角色3.png"),
            };
            SaveSolidImage(characters[0], new Rgba32(220, 30, 30));
            SaveSolidImage(characters[1], new Rgba32(30, 220, 30));
            SaveSolidImage(characters[2], new Rgba32(30, 30, 220));
            var frame = Path.Combine(root, "成片参考.png");
            SaveSolidImage(frame, new Rgba32(220, 180, 30));
            var outputPath = Path.Combine(root, "角色矢量图.png");

            RoleVectorTemplateRenderer.Render(outputPath, characters, [frame]);

            using var output = Image.Load<Rgba32>(outputPath);
            var layout = RoleVectorTemplateRenderer.ResolveLayout(characters.Length);
            using var templateStream = typeof(TikTokReferenceSourcePackageService).Assembly
                .GetManifestResourceStream(layout.ResourceName)!;
            using var template = Image.Load<Rgba32>(templateStream);
            output.Width.Should().Be(RoleVectorTemplateRenderer.CanvasWidth);
            output.Height.Should().Be(RoleVectorTemplateRenderer.CanvasHeight);

            var mask = new bool[output.Width * output.Height];
            foreach (var slot in layout.Groups
                         .SelectMany(group => group.CharacterSlots.Concat(group.ReferenceSlots)))
            {
                for (var y = slot.Top; y < slot.Bottom; y++)
                for (var x = slot.Left; x < slot.Right; x++)
                    mask[y * output.Width + x] = true;
            }

            var outsideMismatchCount = 0;
            for (var y = 0; y < output.Height; y++)
            for (var x = 0; x < output.Width; x++)
            {
                if (!mask[y * output.Width + x] && output[x, y] != template[x, y])
                    outsideMismatchCount++;
            }
            outsideMismatchCount.Should().Be(0, "模板工具栏、节点连线和画布必须原样保留");

            var active = layout.Groups[0].CharacterSlots[0];
            output[active.X + active.Width / 2, active.Y + active.Height / 2].R.Should().BeGreaterThan(200);
            layout.Groups.Should().HaveCount(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Role_vector_renderer_selects_dedicated_three_and_four_character_layouts()
    {
        var three = RoleVectorTemplateRenderer.ResolveLayout(3);
        var four = RoleVectorTemplateRenderer.ResolveLayout(4);
        var five = RoleVectorTemplateRenderer.ResolveLayout(5);
        var six = RoleVectorTemplateRenderer.ResolveLayout(6);

        three.ResourceName.Should().EndWith("RoleVectorTemplate3.png");
        three.Groups.Should().HaveCount(3);
        three.Groups.SelectMany(group => group.CharacterSlots)
            .Min(slot => slot.X).Should().BeGreaterThan(600, "三人布局应整体位于画布中部");

        four.ResourceName.Should().EndWith("RoleVectorTemplate4.png");
        four.Groups.Should().HaveCount(4);
        four.Groups.Take(3).SelectMany(group => group.CharacterSlots)
            .Max(slot => slot.Right).Should().BeLessThan(1200);
        four.Groups[3].CharacterSlots.Min(slot => slot.X)
            .Should().BeGreaterThan(1400, "第四个人物应独立位于右侧中部");

        five.ResourceName.Should().EndWith("RoleVectorTemplate5.png");
        five.Groups.Should().HaveCount(5);
        five.Groups.Take(3).SelectMany(group => group.CharacterSlots)
            .Max(slot => slot.Right).Should().BeLessThan(1200);
        five.Groups.Skip(3).Should().HaveCount(2);

        six.ResourceName.Should().EndWith("RoleVectorTemplate.png");
        six.Groups.Should().HaveCount(6);

        var tooFew = () => RoleVectorTemplateRenderer.ResolveLayout(2);
        var tooMany = () => RoleVectorTemplateRenderer.ResolveLayout(7);
        tooFew.Should().Throw<ArgumentOutOfRangeException>();
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Normalize_character_profiles_clamps_to_three_through_six_and_prioritizes_leads()
    {
        var tooFew = new[]
        {
            new TikTokReferenceSourcePackageService.CharacterProfile("甲", "普通人物"),
            new TikTokReferenceSourcePackageService.CharacterProfile("乙", "普通人物"),
        };
        TikTokReferenceSourcePackageService.NormalizeCharacterProfiles(tooFew, "甲乙共同经历危机。")
            .Should().HaveCount(3);

        var tooMany = Enumerable.Range(1, 7)
            .Select(index => new TikTokReferenceSourcePackageService.CharacterProfile(
                $"角色{index}",
                index == 7 ? "男主，故事核心" : "普通配角"))
            .ToArray();
        var selected = TikTokReferenceSourcePackageService.NormalizeCharacterProfiles(tooMany);

        selected.Should().HaveCount(6);
        selected[0].Name.Should().Be("角色7");
    }

    private static void SaveSolidImage(string path, Rgba32? color = null)
    {
        using var image = new Image<Rgba32>(64, 64, color ?? new Rgba32(80, 100, 120));
        image.SaveAsPng(path);
    }
}
