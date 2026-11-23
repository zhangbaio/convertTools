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
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterReferenceCoverTests
{
    [Theory]
    [InlineData("success")]
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
            using (var source = new Image<Rgba32>(16, 9, new Rgba32(200, 100, 0)))
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
                ImageSize = "24x32",
                PosterTitleVerifyEnabled = outcome == "blocking validation unavailable",
                PosterTitleVerifyMode = "blocking",
            }));

            using var generated = new Image<Rgba32>(36, 24, new Rgba32(0, 10, 220));
            await using var responsePng = new MemoryStream();
            await generated.SaveAsPngAsync(responsePng);
            using var http = new HttpClient(new ImageHandler(async (request, ct) =>
            {
                request.RequestUri!.AbsolutePath.Should().Be("/v1/images/generations");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                body.RootElement.GetProperty("image").GetString().Should().Be(
                    $"data:image/png;base64,{Convert.ToBase64String(originalBytes)}");
                body.RootElement.GetProperty("size").GetString().Should().Be("24x32");
                body.RootElement.GetProperty("prompt").GetString().Should().Contain("A New Title").And.Contain("重新构图");
                (await File.ReadAllBytesAsync(output, ct)).Should().Equal(originalBytes);

                if (outcome == "image service failure")
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("mock failure") };
                if (outcome == "canceled")
                    cancellation.Cancel();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { data = new[] { new { b64_json = Convert.ToBase64String(responsePng.ToArray()) } } }),
                };
            }));
            var renamer = new PosterRenamer(new TxtProjectInfoParser(), new ExternalProcessRunner(), http,
                NullLogger<PosterRenamer>.Instance);
            var request = new PosterRenameRequest(directory.FullName, input, output, config, Overwrite: true);
            var action = () => renamer.RenameAsync(request, cancellation.Token);

            if (outcome == "success")
            {
                await action();
                using var result = await Image.LoadAsync<Rgba32>(output);
                (result.Width, result.Height).Should().Be((24, 32));
                result[12, 16].Should().Be(generated[18, 12]);
            }
            else
            {
                if (outcome == "canceled")
                    await action.Should().ThrowAsync<OperationCanceledException>();
                else
                    await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(
                        outcome == "image service failure" ? "*mock failure*" : "*ChatModelEndpoint*");

                (await File.ReadAllBytesAsync(output)).Should().Equal(originalBytes);
            }

            (await File.ReadAllBytesAsync(input)).Should().Equal(originalBytes);
            directory.EnumerateDirectories(".reference-cover-*").Should().BeEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class ImageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
