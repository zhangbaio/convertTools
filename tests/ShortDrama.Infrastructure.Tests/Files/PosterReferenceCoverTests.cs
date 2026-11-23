using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Models;
using ShortDrama.Core.Posters;
using ShortDrama.Infrastructure.Files;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Process;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterReferenceCoverTests
{
    [Fact]
    public void Difference_ignores_small_zoom_color_and_independent_title_boxes()
    {
        using var original = CreateScene(160, 160, horizontal: false);
        using var candidate = original.Clone(context => context.Crop(new Rectangle(5, 5, 150, 150)).Resize(160, 160));
        candidate.ProcessPixelRows(pixels =>
        {
            for (var y = 0; y < pixels.Height; y++)
            {
                var row = pixels.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var intensity = row[x].R;
                    row[x] = new Rgba32(intensity, (byte)(intensity * 0.7f), (byte)(255 - intensity));
                    if (x is < 8 or >= 152 || y is < 8 or >= 152 || (y is >= 78 and <= 116 && x is >= 25 and <= 135))
                        row[x] = new Rgba32(220, 30, 80);
                }
            }
        });

        var candidateTitle = new RectangleF(25 / 160f, 78 / 160f, 111 / 160f, 39 / 160f);
        PosterCoverDifference.Measure(original, candidate, candidateTitle: candidateTitle)
            .Should().BeLessThan(PosterReferenceCover.MinimumChangedArea);
    }

    [Fact]
    public void Difference_ignores_a_title_only_change_outside_the_usual_title_area()
    {
        using var original = CreateScene(160, 160, horizontal: false);
        using var candidate = original.Clone();
        candidate.ProcessPixelRows(pixels =>
        {
            for (var y = 22; y < 96; y++)
                pixels.GetRowSpan(y).Slice(20, 120).Fill(new Rgba32(240, 110, 20));
        });

        var title = new RectangleF(20 / 160f, 22 / 160f, 120 / 160f, 74 / 160f);
        PosterCoverDifference.Measure(original, candidate, title, title).Should().Be(0);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("second candidate succeeds")]
    [InlineData("both similar")]
    [InlineData("image service failure")]
    [InlineData("blocking validation unavailable")]
    [InlineData("canceled")]
    public async Task Uses_original_as_reference_and_only_commits_an_accepted_image(string outcome)
    {
        var directory = Directory.CreateTempSubdirectory("poster-reference-test-");
        var input = Path.Combine(directory.FullName, "海报原图.png");
        var output = Path.Combine(directory.FullName, "海报图片.png");
        var config = Path.Combine(directory.FullName, "poster.json");
        using var cancellation = new CancellationTokenSource();

        try
        {
            using (var source = CreateScene(128, 96, horizontal: false))
                await source.SaveAsPngAsync(input);
            var originalBytes = await File.ReadAllBytesAsync(input);
            await File.WriteAllBytesAsync(output, originalBytes);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "短剧信息.txt"), "原剧名：原剧\n新剧名：A New Title");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                PosterMode = PosterReferenceCover.Mode,
                ImageModelEndpoint = "https://model.example/v1",
                ImageEditPath = "/images/generations",
                ImageModelId = "test-model",
                ImageModelApiKey = "fake-key",
                ImageSize = "96x128",
                PosterTitleVerifyEnabled = outcome == "blocking validation unavailable",
                PosterTitleVerifyMode = "blocking",
            }));

            using var generated = CreateScene(144, 96, horizontal: true);
            await using var responsePng = new MemoryStream();
            await generated.SaveAsPngAsync(responsePng);
            var attempts = 0;
            using var http = new HttpClient(new ImageHandler(async (request, ct) =>
            {
                attempts++;
                request.RequestUri!.AbsolutePath.Should().Be("/v1/images/generations");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                body.RootElement.GetProperty("image").GetString().Should().Be(
                    $"data:image/png;base64,{Convert.ToBase64String(originalBytes)}");
                body.RootElement.GetProperty("size").GetString().Should().Be("96x128");
                var prompt = body.RootElement.GetProperty("prompt").GetString();
                prompt.Should().Contain("A New Title").And.Contain("40%");
                if (attempts == 2)
                    prompt.Should().Contain(PosterReferenceCover.AlternateComposition);
                (await File.ReadAllBytesAsync(output, ct)).Should().Equal(originalBytes);

                if (outcome == "image service failure")
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("mock failure") };
                if (outcome == "canceled")
                    cancellation.Cancel();

                var candidateBytes = outcome == "both similar" || (outcome == "second candidate succeeds" && attempts == 1)
                    ? originalBytes : responsePng.ToArray();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { data = new[] { new { b64_json = Convert.ToBase64String(candidateBytes) } } }),
                };
            }));
            var renamer = new PosterRenamer(new TxtProjectInfoParser(), new ExternalProcessRunner(), http,
                NullLogger<PosterRenamer>.Instance);
            var request = new PosterRenameRequest(directory.FullName, input, output, config, Overwrite: true);
            var action = () => renamer.RenameAsync(request, cancellation.Token);

            if (outcome is "success" or "second candidate succeeds")
            {
                await action();
                using var result = await Image.LoadAsync<Rgba32>(output);
                (result.Width, result.Height).Should().Be((96, 128));
                result[48, 30].Should().Be(result[66, 30]);
            }
            else
            {
                if (outcome == "canceled")
                    await action.Should().ThrowAsync<OperationCanceledException>();
                else
                    await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(outcome switch
                    {
                        "image service failure" => "*mock failure*",
                        "both similar" => "*最后一次有效画面差异*低于*40*",
                        _ => "*ChatModelEndpoint*",
                    });

                (await File.ReadAllBytesAsync(output)).Should().Equal(originalBytes);
            }

            attempts.Should().Be(outcome is "both similar" or "second candidate succeeds" ? 2 : 1);
            (await File.ReadAllBytesAsync(input)).Should().Equal(originalBytes);
            directory.EnumerateDirectories(".reference-cover-*").Should().BeEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Image<Rgba32> CreateScene(int width, int height, bool horizontal)
    {
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(pixels =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = pixels.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var value = (byte)(30 + 185 * (horizontal ? y / 7 % 2 : x / 9 % 2));
                    row[x] = new Rgba32(value, value, value);
                }
            }
        });
        return image;
    }

    private sealed class ImageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
