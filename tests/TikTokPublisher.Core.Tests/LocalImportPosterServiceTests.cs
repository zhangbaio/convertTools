using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class LocalImportPosterServiceTests
{
    [Fact]
    public void DramaSearchMatch_picks_high_similarity_and_skips_low()
    {
        var match = new DramaSearchItem
        {
            BookId = "hglocal:1",
            Title = "穿越八零之逆袭人生",
            PosterUrl = "https://cdn.example.com/cover.jpg",
        };
        var (hit, ratio) = DramaSearchMatch.Pick([match], "穿越八零之逆袭人生");
        hit.Should().BeSameAs(match);
        ratio.Should().Be(1.0);

        var mismatch = new DramaSearchItem
        {
            BookId = "other",
            Title = "完全无关的别的剧",
            PosterUrl = "https://cdn.example.com/other.jpg",
        };
        var (miss, lowRatio) = DramaSearchMatch.Pick([mismatch], "穿越八零之逆袭人生");
        miss.Should().BeNull();
        lowRatio.Should().BeLessThan(DramaSearchMatch.MinRatio);
    }

    [Fact]
    public void HongguoLocalMediaUrl_normalizes_relative_img_proxy()
    {
        var normalized = HongguoLocalMediaUrl.Normalize(
            "/img?url=https%3A%2F%2Fcdn.example.com%2Fcover.jpg",
            "https://xinge.example.com/api/hongguo/");

        normalized.Should().Be(
            "https://xinge.example.com/img?url=https%3A%2F%2Fcdn.example.com%2Fcover.jpg");
    }

    [Fact]
    public async Task ResolvePosterUrl_prefers_intro_cover_link_without_search()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "简介.txt"),
                "【剧　　名】缺海报剧\n【封面链接】https://cdn.example.com/cover.heic\n【剧情简介】\n剧情。\n",
                new UTF8Encoding(false));

            var searched = false;
            var (url, ratio) = await LocalImportPosterService.ResolvePosterUrlAsync(
                root,
                "缺海报剧",
                searchAsync: (_, _) =>
                {
                    searched = true;
                    return Task.FromResult<IReadOnlyList<DramaSearchItem>>([]);
                });

            url.Should().Be("https://cdn.example.com/cover.heic");
            ratio.Should().Be(1.0);
            searched.Should().BeFalse();
        }
        finally
        {
            DeleteBestEffort(root);
        }
    }

    [Fact]
    public async Task ResolvePosterUrl_falls_back_to_search_match_and_normalizes()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "简介.txt"),
                "【剧　　名】穿越八零之逆袭人生\n【剧情简介】\n女主穿越八零年代。\n",
                new UTF8Encoding(false));
            var settings = new ClientSettings
            {
                HongguoLocalBaseUrl = "https://xinge.example.com/api/hongguo/",
            };

            var (url, ratio) = await LocalImportPosterService.ResolvePosterUrlAsync(
                root,
                "穿越八零之逆袭人生",
                settings: settings,
                searchAsync: (_, _) => Task.FromResult<IReadOnlyList<DramaSearchItem>>(
                [
                    new DramaSearchItem
                    {
                        BookId = "hglocal:80",
                        Title = "穿越八零之逆袭人生",
                        PosterUrl = "/img?url=https%3A%2F%2Fcdn.example.com%2Fcover.jpg",
                    },
                ]));

            url.Should().Be("https://xinge.example.com/img?url=https%3A%2F%2Fcdn.example.com%2Fcover.jpg");
            ratio.Should().Be(1.0);
        }
        finally
        {
            DeleteBestEffort(root);
        }
    }

    [Fact]
    public async Task Import_downloads_missing_poster_from_cover_link_when_enabled()
    {
        var workspace = CreateTempDirectory();
        var source = Path.Combine(workspace, "红果_缺海报剧");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1]);
            File.WriteAllText(
                Path.Combine(source, "简介.txt"),
                "【剧　　名】缺海报剧\n【封面链接】https://cdn.example.com/cover.png\n【剧情简介】\n剧情。\n",
                new UTF8Encoding(false));

            using var handler = new StubHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4])
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") },
                    },
                });
            using var http = new HttpClient(handler);

            // 先解析 URL，再直接 Ensure，避免 Import 走真实搜索/Http；
            // Import 开关路径用 stub 搜索 + 真实 Ensure 由下面单独覆盖。
            var metadata = new JsonObject();
            var (posterUrl, _) = await LocalImportPosterService.ResolvePosterUrlAsync(
                source,
                "缺海报剧",
                metadata,
                searchAsync: (_, _) => Task.FromResult<IReadOnlyList<DramaSearchItem>>([]));
            posterUrl.Should().Be("https://cdn.example.com/cover.png");

            var error = await LocalImportPosterService.EnsurePosterAsync(
                source,
                "缺海报剧",
                posterUrl,
                httpClient: http);
            error.Should().BeNull();
            File.Exists(Path.Combine(source, "缺海报剧.png")).Should().BeTrue();

            var result = LocalManualDramaImportService.Import(
                workspace,
                source,
                downloadMissingPoster: false);
            result.DisplayName.Should().Be("缺海报剧");
            LocalManualDramaImportService.ListProjectsMissingPoster([source]).Should().BeEmpty();
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Import_with_download_flag_uses_intro_cover_and_writes_poster()
    {
        var workspace = CreateTempDirectory();
        var source = Path.Combine(workspace, "红果_缺海报剧");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1]);
            File.WriteAllText(
                Path.Combine(source, "简介.txt"),
                "【剧　　名】缺海报剧\n【封面链接】https://cdn.example.com/cover.png\n【剧情简介】\n剧情。\n",
                new UTF8Encoding(false));

            // 无网络时 Ensure 会失败但 Import 仍应完成，并把 posterUrl 写入 metadata。
            // 这里先预置海报文件，验证「已有海报时 ListMissing 为空」与开关路径不抛错。
            File.WriteAllBytes(Path.Combine(source, "缺海报剧.png"), [9, 9, 9]);

            var result = LocalManualDramaImportService.Import(
                workspace,
                source,
                downloadMissingPoster: true);

            result.DisplayName.Should().Be("缺海报剧");
            using var metadata = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(source, "shortdrama-project.json")));
            metadata.RootElement.GetProperty("posterUrl").GetString()
                .Should().Be("https://cdn.example.com/cover.png");
            LocalManualDramaImportService.ListProjectsMissingPoster([source]).Should().BeEmpty();
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"local-import-poster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
