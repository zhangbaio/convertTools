using FluentAssertions;
using ShortDrama.Infrastructure.Imaging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Imaging;

public sealed class ProjectImageTemplate11Tests
{
    [Fact]
    public void Jianying_draft_name_uses_new_title_episode_number_and_shared_six_digit_token()
    {
        var actual = ProjectImageGenerator.BuildJianyingDraftName(
            "新剧:逆袭/归来",
            "素材文件_第32集",
            trackEpisodeIndex: 1,
            displayToken: 7318);

        actual.Should().Be("新剧_逆袭_归来_第32集_007318");
    }

    [Fact]
    public void Fixed_width_title_keeps_its_declared_viewport_and_clips_only_the_left_prefix()
    {
        using var canvas = new Image<Rgba32>(1920, 1032, new Rgba32(27, 27, 28, 255));
        var rect = new ProjectImageTemplateRegion(
            835,
            7,
            160,
            20,
            "fixed_width=1;text_overflow=clip_left;padding_x=0;font_size=11");
        const string title = "这是一个非常长的新剧名称_第21集_993518";

        ProjectImageGenerator.ExpandTopTitleRect(canvas, rect, title).Should().Be(rect);

        var font = SystemFonts.Families.First().CreateFont(11, FontStyle.Bold);
        var suffix = ProjectImageGenerator.FitTextSuffix(title, font, 160);
        suffix.Should().NotContain("...");
        suffix.Should().EndWith("_第21集_993518");
        suffix.Length.Should().BeLessThan(title.Length);
    }

    [Fact]
    public void Portrait_material_card_preserves_top_middle_and_bottom_with_dark_side_padding()
    {
        using var source = new Image<Rgba32>(90, 160);
        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var color = y < 53
                    ? new Rgba32(240, 20, 20, 255)
                    : y < 107
                        ? new Rgba32(20, 220, 20, 255)
                        : new Rgba32(20, 40, 240, 255);
                accessor.GetRowSpan(y).Fill(color);
            }
        });
        using var canvas = new Image<Rgba32>(180, 130, new Rgba32(100, 90, 80, 255));
        var page = PageWithMaterialRegion(new ProjectImageTemplateRegion(20, 20, 120, 80, "duration=00:05"));

        ProjectImageGenerator.RenderMaterialVideoImages(canvas, page, [source]);

        canvas[80, 22].R.Should().BeGreaterThan(200);
        canvas[80, 60].G.Should().BeGreaterThan(180);
        canvas[80, 97].B.Should().BeGreaterThan(200);
        canvas[23, 60].Should().Be(new Rgba32(7, 7, 9, 255));
    }

    [Fact]
    public void Clipped_last_row_uses_full_card_height_before_cropping_visible_part()
    {
        using var source = new Image<Rgba32>(90, 160);
        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                accessor.GetRowSpan(y).Fill(
                    y < 80 ? new Rgba32(230, 25, 25, 255) : new Rgba32(25, 25, 230, 255));
            }
        });
        using var canvas = new Image<Rgba32>(180, 90, new Rgba32(100, 90, 80, 255));
        var page = PageWithMaterialRegion(
            new ProjectImageTemplateRegion(20, 20, 120, 33, "duration=00:05;card_height=80"));

        ProjectImageGenerator.RenderMaterialVideoImages(canvas, page, [source]);

        canvas[80, 50].R.Should().BeGreaterThan(180);
        canvas[80, 50].B.Should().BeLessThan(80);
    }

    [Fact]
    public void Landscape_material_card_still_covers_the_whole_declared_region()
    {
        using var source = new Image<Rgba32>(160, 90, new Rgba32(240, 120, 15, 255));
        using var canvas = new Image<Rgba32>(180, 130, new Rgba32(10, 10, 10, 255));
        var page = PageWithMaterialRegion(new ProjectImageTemplateRegion(20, 20, 120, 80, "duration=00:05"));

        ProjectImageGenerator.RenderMaterialVideoImages(canvas, page, [source]);

        canvas[21, 60].R.Should().BeGreaterThan(220);
        canvas[138, 60].R.Should().BeGreaterThan(220);
    }

    [Fact]
    public void Aspect_ratio_labels_change_only_declared_regions_for_portrait_and_landscape()
    {
        var page = new ProjectImageTemplatePage(
            "template1.png",
            new Dictionary<string, IReadOnlyList<ProjectImageTemplateRegion>>
            {
                ["draft_aspect_ratio"] =
                [
                    new ProjectImageTemplateRegion(
                        20,
                        20,
                        70,
                        24,
                        "fill=#1B1B1C;text_fill=#FFFFFF;font_size=11;padding_x=5")
                ],
                ["player_aspect_ratio"] =
                [
                    new ProjectImageTemplateRegion(
                        110,
                        20,
                        44,
                        24,
                        "fill=#1B1B1C;badge_fill=#1B1B1C;border=#848484;text_fill=#FFFFFF;font_size=9")
                ]
            });
        using var portrait = new Image<Rgba32>(180, 70, new Rgba32(70, 60, 50, 255));
        using var landscape = portrait.Clone();

        ProjectImageGenerator.RenderAspectRatioLabels(portrait, page, portrait: true);
        ProjectImageGenerator.RenderAspectRatioLabels(landscape, page, portrait: false);

        portrait[5, 5].Should().Be(new Rgba32(70, 60, 50, 255));
        landscape[5, 5].Should().Be(new Rgba32(70, 60, 50, 255));
        RegionPixels(portrait, 20, 20, 134, 24)
            .Should().NotEqual(RegionPixels(landscape, 20, 20, 134, 24));
    }

    private static ProjectImageTemplatePage PageWithMaterialRegion(ProjectImageTemplateRegion rect)
    {
        return new ProjectImageTemplatePage(
            "template1.png",
            new Dictionary<string, IReadOnlyList<ProjectImageTemplateRegion>>
            {
                ["material_video_images"] = [rect]
            });
    }

    private static Rgba32[] RegionPixels(Image<Rgba32> image, int x, int y, int width, int height)
    {
        using var region = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
        var pixels = new Rgba32[width * height];
        region.CopyPixelDataTo(pixels);
        return pixels;
    }
}
