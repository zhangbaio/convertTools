using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoHighCryptoTests
{
    [Fact]
    public void Startup_Envelope_Has_Required_Keys_And_Gcm_Sizes()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var device = new HongguoHighDevice("ab" + new string('c', 22), ecdsa);
        var inner = HongguoHighCrypto.BuildStartupInner(
            device,
            "/auth/login",
            new JsonObject { ["email"] = "a@b.com", ["password"] = "x" },
            "proof");
        var env = HongguoHighCrypto.BuildStartupEnvelope(
            inner,
            "POST",
            "/auth/login",
            encKey: Enumerable.Repeat((byte)0x11, 32).ToArray(),
            signKey: Enumerable.Repeat((byte)0x22, 32).ToArray());

        foreach (var name in HongguoHighCrypto.StartupRequiredKeys)
        {
            env.ContainsKey(name).Should().BeTrue(name);
        }

        env["v"]!.GetValue<int>().Should().Be(1);
        env["app_id"]!.GetValue<string>().Should().Be(HongguoHighCrypto.AppId);
        env["platform"]!.GetValue<string>().Should().Be("win");
        env["alg"]!.GetValue<string>().Should().Be(HongguoHighCrypto.StartupAlg);
        env["kid"]!.GetValue<string>().Should().Be(HongguoHighCrypto.StartupKid);
        env["key_id"]!.GetValue<string>().Should().Be(HongguoHighCrypto.StartupKid);
        env["risk_level"]!.GetValue<string>().Should().Be(HongguoHighCrypto.StartupRiskLevel);
        env["sign"]!.GetValue<string>().Should().HaveLength(64);
        HongguoHighCrypto.FromBase64Url(env["iv"]!.GetValue<string>()).Should().HaveCount(12);
        HongguoHighCrypto.FromBase64Url(env["tag"]!.GetValue<string>()).Should().HaveCount(16);
        HongguoHighCrypto.FromBase64Url(env["nonce"]!.GetValue<string>()).Should().HaveCount(16);
        inner["path"]!.GetValue<string>().Should().Be("/auth/login");
        ((JsonObject)inner["param"]!)["email"]!.GetValue<string>().Should().Be("a@b.com");
        HongguoHighCrypto.ToBase64Url(new byte[12]).Should().HaveLength(16);
        HongguoHighCrypto.ToBase64Url(new byte[16]).Should().HaveLength(22);
        HongguoHighCrypto.ToBase64Url(new byte[64]).Should().HaveLength(86);
    }

    [Fact]
    public void Letter_Envelope_Has_Required_Keys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var device = new HongguoHighDevice("ab" + new string('c', 22), ecdsa);
        var session = new HongguoHighSession
        {
            AccessToken = "tok",
            SessionId = "sid",
            SessionKeyB64 = HongguoHighCrypto.ToBase64Url(RandomNumberGenerator.GetBytes(32)),
            SessionKeyId = "session-v1"
        };
        var inner = HongguoHighCrypto.BuildBusinessInner(
            device,
            session,
            "/video/batch-parse",
            new JsonObject { ["book_id"] = "1" },
            "proof");
        var env = HongguoHighCrypto.BuildLetterEnvelope(device, session, inner, "POST", "/video/batch-parse");

        foreach (var name in HongguoHighCrypto.LetterRequiredKeys)
        {
            env.ContainsKey(name).Should().BeTrue(name);
        }

        env["v"]!.GetValue<int>().Should().Be(2);
        env["a"]!.GetValue<string>().Should().Be(HongguoHighCrypto.AppId);
        env["b"]!.GetValue<string>().Should().Be("win");
        env["c"]!.GetValue<string>().Should().Be(HongguoHighCrypto.ClientVersion);
        env["u"]!.GetValue<string>().Should().Be("session-v1");
        env["j"]!.GetValue<string>().Should().Be("tok");
        env["p"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        env["r"]!.GetValue<string>().Should().HaveLength(64);
    }

    [Fact]
    public void NormalizeQuality_Maps_Common_Labels()
    {
        HongguoHighCrypto.NormalizeQuality("1080P").Should().Be("1080");
        HongguoHighCrypto.NormalizeQuality("1080p+").Should().Be("1080");
        HongguoHighCrypto.NormalizeQuality("720P").Should().Be("720");
    }

    [Fact]
    public void Cng_Ecs2_Roundtrip()
    {
        using var original = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var blob = HongguoHighCrypto.PackEcs2(original);
        using var parsed = HongguoHighCrypto.ParseCngPrivateKey(blob);
        var message = "device-proof-sign-v1\ntest"u8.ToArray();
        var signature = HongguoHighCrypto.SignP1363(parsed, message);
        parsed.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue();
    }

    [Fact]
    public void Masters_Cache_Roundtrip_Does_Not_Use_Weixin_Assistant_Path()
    {
        var previous = HongguoHighDeviceStore.CacheDirectoryOverride;
        var temp = Path.Combine(Path.GetTempPath(), "hghigh-" + Guid.NewGuid().ToString("N"));
        HongguoHighDeviceStore.CacheDirectoryOverride = temp;
        try
        {
            var enc = HongguoHighCrypto.ToBase64Url(Enumerable.Repeat((byte)0x31, 48).ToArray());
            var sign = HongguoHighCrypto.ToBase64Url(Enumerable.Repeat((byte)0x32, 48).ToArray());
            var path = HongguoHighDeviceStore.CacheStartupMasters(enc, sign, "abc123abc123abc123abc123");
            path.Should().Be(Path.Combine(temp, "startup_masters.json"));
            path.Should().NotContain("weixin-channel-tool");
            var loaded = HongguoHighDeviceStore.LoadStartupMastersRaw();
            loaded.Enc.Should().Be(enc);
            loaded.Sign.Should().Be(sign);
            HongguoHighDeviceStore.ClearStartupMasters();
            File.Exists(path).Should().BeFalse();
            var cleared = HongguoHighDeviceStore.LoadStartupMastersRaw();
            cleared.Enc.Should().BeEmpty();
            cleared.Sign.Should().BeEmpty();
        }
        finally
        {
            HongguoHighDeviceStore.CacheDirectoryOverride = previous;
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public void IsOfficialClientPath_Matches_Configured_Exe_And_Default_Folder()
    {
        var exe = Path.Combine(Path.GetTempPath(), "HG-high-bitrate.exe");
        HongguoHighMasterProvisioner.IsOfficialClientPath(exe, exe).Should().BeTrue();
        HongguoHighMasterProvisioner.IsOfficialClientPath(
                Path.Combine(Path.GetTempPath(), "other.exe"),
                exe)
            .Should().BeFalse();

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HongguoHighDownloader",
            "HG短剧下载器高码率版v2.1.6.exe");
        HongguoHighMasterProvisioner.IsOfficialClientPath(folder, configuredExePath: null).Should().BeTrue();
        HongguoHighMasterProvisioner.IsOfficialClientPath(null, exe).Should().BeFalse();
    }

    [Fact]
    public void TryParseMastersJson_Reads_File_Payload_And_Stdout_Prefix()
    {
        var enc = HongguoHighCrypto.ToBase64Url(Enumerable.Repeat((byte)0x41, 48).ToArray());
        var sign = HongguoHighCrypto.ToBase64Url(Enumerable.Repeat((byte)0x42, 48).ToArray());
        var json = $$"""{"ok":true,"enc":"{{enc}}","sign":"{{sign}}"}""";

        HongguoHighMasterProvisioner.TryParseMastersJson(json, out var parsedEnc, out var parsedSign)
            .Should().BeTrue();
        parsedEnc.Should().Be(enc);
        parsedSign.Should().Be(sign);

        HongguoHighMasterProvisioner.TryParseMastersJson("启动中...\nMASTERS_JSON:" + json + "\n", out parsedEnc, out parsedSign)
            .Should().BeTrue();
        parsedEnc.Should().Be(enc);
        parsedSign.Should().Be(sign);

        var temp = Path.Combine(Path.GetTempPath(), "hghigh-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temp, json);
            HongguoHighMasterProvisioner.TryReadMastersFile(temp, out parsedEnc, out parsedSign).Should().BeTrue();
            parsedEnc.Should().Be(enc);
            parsedSign.Should().Be(sign);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

public sealed class HongguoHighCalendarMapperTests
{
    [Fact]
    public void MapPayload_Reads_Nested_Book_Info_And_Skips_Zero_Online_Time()
    {
        var payload = JsonNode.Parse("""
            {
              "type": "calendarResults",
              "items": [
                {
                  "online_time": "0",
                  "cache_date": "20260818",
                  "book_info": {
                    "book_id": "nested-1",
                    "book_name": "嵌套漫剧",
                    "author": "作者甲",
                    "chapter_number": 24,
                    "first_online_time": "2026-08-18 09:00:00",
                    "abstract": "简介",
                    "thumb_url": "https://cdn.example.com/nested.jpg"
                  }
                }
              ]
            }
            """)!;

        var items = HongguoHighCalendarMapper.MapPayload(payload);
        items.Should().ContainSingle();
        items[0].BookId.Should().Be("hghigh:nested-1");
        items[0].Title.Should().Be("嵌套漫剧");
        items[0].Author.Should().Be("作者甲");
        items[0].EpisodeTotal.Should().Be(24);
        items[0].PublishTime.Should().Be("2026-08-18 09:00:00");
        items[0].Intro.Should().Be("简介");
        items[0].PosterUrl.Should().Be("https://cdn.example.com/nested.jpg");
    }

    [Fact]
    public void ApplyBookInfo_Fills_Author_Episodes_And_Clock()
    {
        var item = new DramaSearchItem("hghigh:1", "剧名", "", 0, "", "", "", "2026-08-18 00:00:00");
        var info = JsonNode.Parse("""
            {
              "author": "tollgela3429",
              "chapter_number": 36,
              "create_time": "2026-08-18T11:05:01+08:00"
            }
            """)!.AsObject();

        var applied = HongguoHighCalendarMapper.ApplyBookInfo(item, info);
        applied.Author.Should().Be("tollgela3429");
        applied.EpisodeTotal.Should().Be(36);
        var expected = DateTimeOffset.Parse("2026-08-18T11:05:01+08:00").LocalDateTime
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        applied.PublishTime.Should().Be(expected);
    }

    [Fact]
    public void ExtractLandpageItems_Reads_Video_Data_Series()
    {
        var payload = JsonNode.Parse("""
            {
              "cell_view": [
                {
                  "video_data": {
                    "series_id": "ai-1",
                    "series_title": "AI剧",
                    "author": "作者乙",
                    "first_online_time": "2026-08-18 12:00:00"
                  }
                }
              ]
            }
            """)!;

        var raw = HongguoHighCalendarMapper.ExtractLandpageItems(payload);
        var mapped = HongguoHighCalendarMapper.TryMapItem(raw[0]);
        mapped.Should().NotBeNull();
        mapped!.BookId.Should().Be("hghigh:ai-1");
        mapped.Title.Should().Be("AI剧");
        mapped.Author.Should().Be("作者乙");
        mapped.PublishTime.Should().Be("2026-08-18 12:00:00");
    }
}

public sealed class HongguoHighDramaChainTests
{
    [Fact]
    public async Task SearchAsync_Uses_Novelfm_Host_And_Hghigh_Prefix()
    {
        var handler = new HighSearchHandler();
        using var httpClient = new HttpClient(handler);
        var settings = new DramaSourceSettings
        {
            DramaSourceChain = "hghigh",
            HghighAccount = "high@example.com",
            HghighPassword = "secret"
        };
        var router = new DramaSourceRouter(
            httpClient,
            new StaticSettings(settings),
            new HongguoLocalApiService(httpClient),
            new HongguoNewApiService(httpClient),
            new HongguoDramaSearchService(httpClient),
            new HongguoDramaDownloader(httpClient),
            new HongguoMemoryReaderService());

        var results = await router.SearchAsync("测试高码率", 1, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Title.Should().Be("高码率剧");
        results[0].BookId.Should().Be("hghigh:123456");
        results[0].PosterUrl.Should().Be("https://cover");
        handler.Hosts.Should().Contain("api5-sinfonlinea.novelfm.com");
        handler.Hosts.Should().NotContain("au.s1o.cc");
        handler.Hosts.Should().NotContain("m.iusc.cc");
    }

    [Fact]
    public async Task GetEpisodesAsync_Uses_Fanqie_Directory_And_Source_Index()
    {
        var handler = new HighDirectoryHandler();
        using var httpClient = new HttpClient(handler);
        var service = new HongguoHighApiService(httpClient);

        var episodes = await service.GetEpisodesAsync(
            new DramaSourceSettings { DramaSourceChain = "hghigh" },
            "hghigh:999",
            CancellationToken.None);

        episodes.Should().HaveCount(2);
        episodes[0].EpisodeNumber.Should().Be(1);
        episodes[0].Title.Should().Be("第1集");
        episodes[1].EpisodeNumber.Should().Be(2);
        HongguoHighCrypto.TryDecodeEpisodeId(episodes[0].VideoId, out var bookId, out var number, out var videoId)
            .Should().BeTrue();
        bookId.Should().Be("999");
        number.Should().Be(1);
        videoId.Should().Be("vid-a");
        handler.LastUri!.Host.Should().Be("api-sinfonlinec.fanqiesdk.com");
        handler.LastUri.Query.Should().Contain("book_id=999");
        handler.LastUri.Query.Should().Contain("aid=1967");
    }

    [Fact]
    public async Task GetTodayAsync_Throws_For_Hghigh_Without_Falling_Back()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var settings = new DramaSourceSettings { DramaSourceChain = "hghigh" };
        var router = new DramaSourceRouter(
            httpClient,
            new StaticSettings(settings),
            new HongguoLocalApiService(httpClient),
            new HongguoNewApiService(httpClient),
            new HongguoDramaSearchService(httpClient),
            new HongguoDramaDownloader(httpClient),
            new HongguoMemoryReaderService());

        var act = () => router.GetTodayAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*暂不支持短剧今日上新*");
    }

    private sealed class StaticSettings : IDramaSettingsProvider
    {
        private readonly DramaSourceSettings _settings;

        public StaticSettings(DramaSourceSettings settings) => _settings = settings;

        public DramaSourceSettings Get() => _settings;

        public void SavePikachuDeviceId(string deviceId)
        {
        }
    }

    private sealed class HighSearchHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            var json = """
                {"code":0,"data":{"search_data":[{"books":[{"book_id":"123456","book_name":"高码率剧","author":"甲","audio_thumb_uri":"https://cover","abstract":"简介","category":"漫剧"}]}]}}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class HighDirectoryHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            var json = """
                {"code":0,"data":{"item_list":["vid-a","vid-b"],"book_info":{"book_name":"测试剧","chapter_number":2}}}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class FailAllHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"高码率短剧上新不应发请求：{request.RequestUri}");
    }
}
