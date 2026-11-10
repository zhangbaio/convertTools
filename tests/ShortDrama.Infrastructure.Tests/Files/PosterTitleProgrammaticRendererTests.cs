using FluentAssertions;
using ShortDrama.Infrastructure.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterTitleProgrammaticRendererTests
{
    [Theory]
    [InlineData("灵泉空间", "灵泉空间")]
    [InlineData("携灵泉空间在古代创富安家", "携灵泉空间\n在古代创富安家")]
    [InlineData("第一行\n第二行", "第一行\n第二行")]
    public void FormatTitleLines_creates_readable_poster_lines(string title, string expected)
    {
        PosterTitleProgrammaticRenderer.FormatTitleLines(title).Should().Be(expected);
    }

    [Fact]
    public void CreateFixedTemplateLayout_uses_reference_position_and_size()
    {
        var layout = PosterTitleProgrammaticRenderer.CreateFixedTemplateLayout(
            PosterTitleProgrammaticRenderer.FormatTitleLines("签约错认旧爱再续前缘"),
            600,
            858);

        layout.X.Should().BeApproximately(0.07f, 0.001f);
        layout.Y.Should().BeApproximately(0.663f, 0.01f);
        layout.Width.Should().BeApproximately(0.8f, 0.001f);
        layout.FontScale.Should().BeApproximately(64f / 858f, 0.001f);
        layout.TextColor.Should().Be(new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 255, 255, 255));
        layout.Align.Should().Be(SixLabors.Fonts.HorizontalAlignment.Left);
    }

    [Fact]
    public void FormatLowerHalfLines_keeps_latin_words()
    {
        PosterTitleProgrammaticRenderer.FormatLowerHalfLines("O Bilionário que Vivia como Sem-Teto")
            .Replace("\n", " ")
            .Should().Be("O Bilionário que Vivia como Sem-Teto");
    }

    [Theory]
    [InlineData("The Bride Who Took Her Sister's Vow")]
    [InlineData("O Bilionário que Vivia como Sem-Teto")]
    [InlineData("L'amour secret de sa sœur")]
    [InlineData("Die Donaudampfschifffahrtsgesellschaft")]
    [InlineData("Người vợ lấy lời thề của chị")]
    [InlineData("La hermana que rompió su voto")]
    [InlineData("Sumpah saudara perempuannya")]
    [InlineData("그녀의 자매가 한 맹세")]
    [InlineData("عروس أخذت نذر أختها")]
    public void Spaced_non_chinese_titles_wrap_on_word_boundaries(string title)
    {
        var laid = PosterTitleProgrammaticRenderer.FormatMeasuredPosterLines(title, 90, text => text.Length * 10f);

        laid.Replace("\n", " ").Should().Be(title);
        laid.Should().NotBe(PosterTitleProgrammaticRenderer.FormatTitleLines(title));
        foreach (var word in title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            laid.Split('\n').SelectMany(line => line.Split(' ')).Should().Contain(word);
    }

    [Fact]
    public void Camel_case_latin_title_regains_word_spaces()
    {
        const string title = "TheBrideWhoTookHerSister'sVow";

        PosterTitleProgrammaticRenderer.FormatMeasuredPosterLines(title, 160, text => text.Length * 10f)
            .Replace("\n", " ")
            .Should().Be("The Bride Who Took Her Sister's Vow");
        PosterTitleProgrammaticRenderer.FormatLowerHalfLines(title)
            .Replace("\n", " ")
            .Should().Be("The Bride Who Took Her Sister's Vow");
    }

    [Fact]
    public void English_title_is_not_cut_inside_a_word()
    {
        const string title = "The Bride Who Took Her Sister's Vow";
        var laid = PosterTitleProgrammaticRenderer.FormatMeasuredPosterLines(title, 140, text => text.Length * 10f);

        laid.Should().NotContain("TheBrideWh");
        laid.Split('\n').Should().OnlyContain(line => title.Contains(line, StringComparison.Ordinal));
    }

    [Fact]
    public void German_compound_stays_on_one_line()
    {
        var laid = PosterTitleProgrammaticRenderer.FormatMeasuredPosterLines(
            "Die Donaudampfschifffahrtsgesellschaft",
            40,
            text => text.Length * 10f);

        laid.Split('\n').Should().Contain("Donaudampfschifffahrtsgesellschaft");
    }

    [Fact]
    public void Japanese_kana_keeps_spaces_and_small_kana_on_the_previous_line()
    {
        PosterTitleProgrammaticRenderer.ClassifyTitleScript("秘密 の話").Should().Be(PosterTitleScript.Japanese);
        PosterTitleProgrammaticRenderer.FormatLowerHalfLines("秘密 の話")
            .Replace("\n", " ")
            .Should().Be("秘密 の話");

        var laid = PosterTitleProgrammaticRenderer.FormatMeasuredPosterLines("ちょっと", 25, text => text.Length * 10f);
        string.Concat(laid.Split('\n')).Should().Be("ちょっと");
        laid.Split('\n').Should().OnlyContain(line => !line.StartsWith('っ') && !line.StartsWith('ょ'));
    }

    [Fact]
    public void Korean_hangul_is_not_laid_out_as_chinese()
    {
        PosterTitleProgrammaticRenderer.ClassifyTitleScript("그녀의 맹세").Should().Be(PosterTitleScript.Korean);
        PosterTitleProgrammaticRenderer.FormatLowerHalfLines("그녀의 맹세")
            .Replace("\n", " ")
            .Should().Be("그녀의 맹세");
    }

    [Fact]
    public void Thai_leading_vowel_stays_with_the_following_cluster()
    {
        PosterTitleProgrammaticRenderer.ClassifyTitleScript("ก็ได้").Should().Be(PosterTitleScript.Thai);
        var laid = PosterTitleProgrammaticRenderer.FormatMeasuredPosterLines("ก็ได้", 15, text => text.Length * 10f);

        string.Concat(laid.Split('\n')).Should().Be("ก็ได้");
        laid.Split('\n').Should().NotContain(line => line.EndsWith('ไ') || line.StartsWith('้') || line.StartsWith('็'));
    }

    [Fact]
    public void Arabic_title_is_right_to_left_and_keeps_word_spaces()
    {
        PosterTitleProgrammaticRenderer.ClassifyTitleScript("عروس أخذت نذر أختها").Should().Be(PosterTitleScript.Arabic);
        PosterTitleProgrammaticRenderer.FormatLowerHalfLines("عروس أخذت نذر أختها")
            .Replace("\n", " ")
            .Should().Be("عروس أخذت نذر أختها");
    }

    [Theory]
    [InlineData("The Bride Who Took Her Sister's Vow")]
    [InlineData("TheBrideWhoTookHerSister'sVow")]
    [InlineData("彼女の秘密")]
    [InlineData("그녀의 맹세")]
    [InlineData("ก็ได้")]
    [InlineData("عروس أخذت نذر أختها")]
    [InlineData("携灵泉空间在古代创富安家")]
    public void Lower_half_render_writes_each_script(string title)
    {
        var root = Path.Combine(Path.GetTempPath(), "poster-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "in.png");
            var output = Path.Combine(root, "out.png");
            using (var image = new Image<Rgba32>(600, 858, new Rgba32(20, 20, 20, 255)))
                image.SaveAsPng(input);

            PosterTitleProgrammaticRenderer.RenderLowerHalf(input, output, title);

            using var rendered = Image.Load<Rgba32>(output);
            rendered.Width.Should().Be(600);
            rendered.Height.Should().Be(858);
            CountBrightLowerPixels(rendered).Should().BeGreaterThan(30);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static int CountBrightLowerPixels(Image<Rgba32> image)
    {
        var count = 0;
        var start = image.Height / 2;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = start; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].R > 180 && row[x].G > 140)
                        count++;
                }
            }
        });
        return count;
    }

    [Fact]
    public void Chinese_titles_still_use_character_wrapping()
    {
        PosterTitleProgrammaticRenderer.ClassifyTitleScript("携灵泉空间在古代创富安家").Should().Be(PosterTitleScript.Chinese);
        PosterTitleProgrammaticRenderer.FormatLowerHalfLines("携灵泉空间在古代创富安家")
            .Should().Be(PosterTitleProgrammaticRenderer.FormatTitleLines("携灵泉空间在古代创富安家"));
        PosterTitleProgrammaticRenderer.ClassifyTitleScript("東京物語").Should().Be(PosterTitleScript.Chinese);
    }

    [Fact]
    public void CreateLowerHalfLayout_places_yellow_title_in_the_lower_half()
    {
        var layout = PosterTitleProgrammaticRenderer.CreateLowerHalfLayout("O Bilionário\nque Vivia", 600, 858);

        layout.Y.Should().BeGreaterThanOrEqualTo(0.50f);
        layout.Width.Should().BeApproximately(0.86f, 0.001f);
        layout.TextColor.Should().Be(new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 214, 0, 255));
        layout.Align.Should().Be(SixLabors.Fonts.HorizontalAlignment.Center);
    }
}
