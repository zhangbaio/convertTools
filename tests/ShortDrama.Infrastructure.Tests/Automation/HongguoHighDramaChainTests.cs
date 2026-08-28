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
    public void MapPayload_Reads_Original_App_Cover_Shapes_And_Normalizes_Byteimg_Template()
    {
        var payload = JsonNode.Parse("""
            {
              "items": [
                {
                  "video_data": {
                    "series_id": "ai-cover-1",
                    "series_title": "AI封面剧",
                    "seriesCover": {
                      "urlList": [
                        "https://p3-novel.byteimg.com/img/novel-pic/abc123~tplv-resize:200:300.image?x=1"
                      ]
                    }
                  }
                },
                {
                  "bookInfo": {
                    "bookId": "ai-cover-2",
                    "bookName": "备用封面剧",
                    "bookCover": {
                      "urls": ["//p3-novel.byteimg.com/origin/novel-pic/def456"]
                    }
                  }
                }
              ]
            }
            """)!;

        var items = HongguoHighCalendarMapper.MapPayload(payload);

        items.Should().HaveCount(2);
        items[0].PosterUrl.Should().Be("https://p3-novel.byteimg.com/origin/novel-pic/abc123");
        items[1].PosterUrl.Should().Be("https://p3-novel.byteimg.com/origin/novel-pic/def456");
    }

    [Theory]
    [InlineData("file:///tmp/poster.jpg")]
    [InlineData("novel-pic/abc123")]
    [InlineData("")]
    public void NormalizeMediaUrl_Rejects_NonHttp_Values(string value)
    {
        HongguoHighCalendarMapper.NormalizeMediaUrl(value).Should().BeNull();
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
    public void ApplyBookInfo_Uses_Authoritative_Episode_Count_When_Chapter_Number_Lags()
    {
        var item = new DramaSearchItem("hghigh:1", "剧名", "", 80, "", "", "", "");
        var info = JsonNode.Parse("""
            {
              "chapter_number": 80,
              "serial_count": 999,
              "final_chapter_number": 81,
              "drama_chapter_number": 81,
              "last_chapter_title": "第81集"
            }
            """)!.AsObject();

        var applied = HongguoHighCalendarMapper.ApplyBookInfo(item, info);

        applied.EpisodeTotal.Should().Be(81);
    }

    [Fact]
    public void Calendar_enrichment_is_required_when_only_poster_is_missing()
    {
        var missingPoster = new DramaSearchItem(
            "hghigh:1",
            "剧名",
            "都市日常",
            36,
            "简介",
            "",
            "作者甲",
            "2026-08-24 10:00:00");
        var complete = missingPoster with { PosterUrl = "https://cdn.example.com/poster.jpg" };

        HongguoHighApiService.NeedsCalendarEnrichment(missingPoster).Should().BeTrue();
        HongguoHighApiService.NeedsCalendarEnrichment(complete).Should().BeFalse();
    }

    [Fact]
    public void ReadEpisodeTotal_Uses_Drama_Count_Before_Stale_Or_Unrelated_Counts()
    {
        var info = JsonNode.Parse("""
            {
              "drama_chapter_number": 62,
              "final_chapter_number": 62,
              "serial_count": 999,
              "chapter_number": 61
            }
            """)!.AsObject();

        HongguoHighCalendarMapper.ReadEpisodeTotal(info).Should().Be(62);
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

    [Fact]
    public void ExtractLandpageItems_Preserves_Outer_Cover_Alongside_VideoData()
    {
        var payload = JsonNode.Parse("""
            {
              "cell_view": [
                {
                  "seriesCover": {
                    "urlList": ["https://p3-novel.byteimg.com/origin/novel-pic/outer-cover"]
                  },
                  "videoData": {
                    "seriesId": "ai-outer-cover",
                    "series_title": "外层封面剧"
                  }
                }
              ]
            }
            """)!;

        var raw = HongguoHighCalendarMapper.ExtractLandpageItems(payload);
        var mapped = HongguoHighCalendarMapper.TryMapItem(raw.Single());

        mapped.Should().NotBeNull();
        mapped!.BookId.Should().Be("hghigh:ai-outer-cover");
        mapped.PosterUrl.Should().Be("https://p3-novel.byteimg.com/origin/novel-pic/outer-cover");
    }

    [Fact]
    public void MapPayload_Recursively_Finds_ImageUrl_In_Unknown_Outer_Shape()
    {
        var payload = JsonNode.Parse("""
            {
              "items": [
                {
                  "book_id": "deep-cover",
                  "book_name": "深层封面剧",
                  "render_meta": {
                    "artwork": {
                      "resources": [
                        "https://p3-novel.byteimg.com/origin/novel-pic/deep-cover-image"
                      ]
                    }
                  }
                }
              ]
            }
            """)!;

        var mapped = HongguoHighCalendarMapper.MapPayload(payload).Single();

        mapped.PosterUrl.Should().Be("https://p3-novel.byteimg.com/origin/novel-pic/deep-cover-image");
    }
}

public sealed class HongguoHighDramaChainTests
{
    [Fact]
    public void SpadeKey_Unwraps_Known_Hongguo_Sample()
    {
        HongguoSpadeKey.UnwrapCandidates("rLwi9m+PFvVZjiLebZM82HKUCN5YlhPtQqcJ6keXC+h1kz2fnw==")
            .Should().Contain("a46cbc7ed9769356c3813c83355e3fde");
    }

    [Fact]
    public async Task EnsureTokenAsync_Coalesces_Concurrent_Initial_Logins()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loginCalls = 0;
        service.LoginForTests = async (settings, cancellationToken) =>
        {
            Interlocked.Increment(ref loginCalls);
            loginStarted.TrySetResult();
            await releaseLogin.Task.WaitAsync(cancellationToken);
            return new JsonObject
            {
                ["accessToken"] = "shared-token",
                ["sessionId"] = "shared-session",
                ["sessionKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray())
            };
        };
        var settings = new DramaSourceSettings
        {
            HghighAccount = "high@example.com",
            HghighPassword = "secret"
        };

        var requests = Enumerable.Range(0, 8)
            .Select(_ => service.EnsureTokenForTestsAsync(settings, CancellationToken.None))
            .ToArray();
        await loginStarted.Task;
        releaseLogin.TrySetResult();
        await Task.WhenAll(requests);

        loginCalls.Should().Be(1);
    }

    [Fact]
    public void ShouldRelogin_Recognizes_Session_Credential_Mismatch()
    {
        HongguoHighApiService.ShouldRelogin(new HongguoHighException("会话凭证不一致", 422))
            .Should().BeTrue();
    }

    [Fact]
    public async Task SearchAsync_Uses_V216_Search_Tab_And_Authoritative_Video_Episode_Count()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var settings = new DramaSourceSettings
        {
            DramaSourceChain = "hghigh",
            HghighAccount = "high@example.com",
            HghighPassword = "secret"
        };
        JsonObject? capturedSpec = null;
        service.AuthedRequestForTests = (_, path, spec, _, _) =>
        {
            path.Should().Be("/redguo/sign");
            capturedSpec = spec.DeepClone().AsObject();
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["host"] = "api5-normal-sinfonlinea.fqnovel.com",
                ["path"] = "/reading/bookapi/search/tab/v",
                ["method"] = "GET",
            });
        };
        service.ExecuteSignedRequestForTests = (_, body, _, _) =>
        {
            body.Should().BeEmpty();
            return Task.FromResult<JsonNode?>(JsonNode.Parse("""
                {
                  "data": {
                    "search_data": [{
                      "book_id": "7677524795017137177",
                      "drama_chapter_number": 60,
                      "video_data": [{
                        "series_id": "7677524795017137177",
                        "title": "陆总，迟来的深情我不要",
                        "episode_cnt": 58,
                        "video_detail": {
                          "episode_cnt": 58,
                          "episode_right_text": "共58集",
                          "series_intro": "简介",
                          "series_cover": "https://cdn.example.com/cover"
                        }
                      }]
                    }]
                  }
                }
                """));
        };

        var results = await service.SearchAsync(settings, "陆总，迟来的深情我不要", 1, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Title.Should().Be("陆总，迟来的深情我不要");
        results[0].BookId.Should().Be("hghigh:7677524795017137177");
        results[0].EpisodeTotal.Should().Be(58);
        results[0].PosterUrl.Should().Be("https://cdn.example.com/cover");
        capturedSpec.Should().NotBeNull();
        capturedSpec!["host"]!.GetValue<string>().Should().Be("api5-normal-sinfonlinea.fqnovel.com");
        capturedSpec["path"]!.GetValue<string>().Should().Be("/reading/bookapi/search/tab/v");
        capturedSpec["method"]!.GetValue<string>().Should().Be("GET");
        var parameters = capturedSpec["params"]!.AsObject();
        parameters["query"]!.GetValue<string>().Should().Be("陆总，迟来的深情我不要");
        parameters["tab_type"]!.GetValue<int>().Should().Be(11);
        parameters["offset"]!.GetValue<int>().Should().Be(0);
        parameters["count"]!.GetValue<int>().Should().Be(20);
    }

    [Fact]
    public async Task SearchAsync_Falls_Back_To_Novelfm_When_V216_Signing_Is_Unavailable()
    {
        var handler = new HighSearchAndDirectoryHandler();
        using var httpClient = new HttpClient(handler);
        var service = new HongguoHighApiService(httpClient)
        {
            AuthedRequestForTests = (_, _, _, _, _) => throw new HongguoHighException("sign unavailable")
        };

        var results = await service.SearchAsync(
            new DramaSourceSettings(),
            "测试高码率",
            1,
            CancellationToken.None);

        results.Should().ContainSingle();
        results[0].EpisodeTotal.Should().Be(58);
        handler.Hosts.Should().Contain("api5-sinfonlinea.novelfm.com");
        handler.Hosts.Should().Contain("api-sinfonlinec.fanqiesdk.com");
    }

    [Fact]
    public void Fanqie_Query_Encoding_Matches_The_V216_Client()
    {
        HongguoHighApiService.EscapeFanqieQueryValue("1080*2400").Should().Be("1080*2400");
        HongguoHighApiService.EscapeFanqieQueryValue("clks####11@123")
            .Should().Be("clks%23%23%23%2311%40123");
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
    public async Task GetVideoPlaybackAsync_Uses_Three_Short_Attempts_For_Read_Timeout()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var calls = 0;
        service.AuthedRequestForTests = (_, path, _, timeout, _) =>
        {
            path.Should().Be("/video/batch-parse");
            timeout.Should().Be(15);
            calls++;
            throw new TaskCanceledException("read timeout");
        };
        service.DelayForTests = (_, _) => Task.CompletedTask;

        var act = () => service.GetVideoPlaybackAsync(
            new DramaSourceSettings { HongguoDownloadTimeoutSeconds = "60" },
            HongguoHighCrypto.EncodeEpisodeId("book-1", 19, "vid-19"),
            "1080P",
            CancellationToken.None);

        await act.Should().ThrowAsync<HongguoHighException>()
            .WithMessage("*解析超过 15 秒*");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task GetVideoPlaybackAsync_Uses_One_Batch_Request_For_Planned_Concurrent_Episodes()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var settings = new DramaSourceSettings
        {
            HghighAccount = "batch@example.com",
            HghighDeviceId = "device-1"
        };
        var encodedIds = Enumerable.Range(1, 3)
            .Select(number => HongguoHighCrypto.EncodeEpisodeId("book-1", number, $"vid-{number}"))
            .ToArray();
        var calls = 0;
        service.AuthedRequestForTests = (_, path, payload, _, _) =>
        {
            path.Should().Be("/video/batch-parse");
            Interlocked.Increment(ref calls);
            payload["episodes"]!.AsArray().Should().HaveCount(3);
            return Task.FromResult<JsonNode?>(new JsonArray(
                Enumerable.Range(1, 3)
                    .Select(number => (JsonNode)new JsonObject
                    {
                        ["episodeId"] = $"vid-{number}",
                        ["downloadUrl"] = $"https://cdn.example.com/{number}.mp4",
                        ["encrypted_url"] = $"https://origin.example.com/{number}.mp4",
                        ["spade_a"] = "spade-value",
                        ["encrypt"] = true,
                        ["sizeBytes"] = number * 100L
                    })
                    .ToArray()));
        };

        using var plan = service.RegisterBatchParsePlan(settings, encodedIds, "1080P", batchSize: 3);
        var results = await Task.WhenAll(encodedIds.Select(id =>
            service.GetVideoPlaybackAsync(settings, id, "1080P", CancellationToken.None)));

        calls.Should().Be(1);
        results.Select(item => item.Url).Should().Equal(
            "https://cdn.example.com/1.mp4",
            "https://cdn.example.com/2.mp4",
            "https://cdn.example.com/3.mp4");
        results.Should().OnlyContain(item =>
            item.EncryptedUrls.Count == 1 && item.SpadeA == "spade-value" && item.Encrypted);

        plan.Dispose();
        var cached = await service.GetVideoPlaybackAsync(settings, encodedIds[0], "1080P", CancellationToken.None);
        cached.Url.Should().Be("https://cdn.example.com/1.mp4");
        calls.Should().Be(1, "短期播放地址缓存不应重复调用慢解析接口");
    }

    [Fact]
    public async Task GetVideoPlaybackAsync_Retries_Empty_Address_And_Matches_Episode()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var calls = 0;
        var delays = new List<TimeSpan>();
        service.AuthedRequestForTests = (_, path, _, _, _) =>
        {
            path.Should().Be("/video/batch-parse");
            calls++;
            JsonNode response = calls == 1
                ? new JsonArray
                {
                    new JsonObject
                    {
                        ["episodeId"] = "vid-81",
                        ["message"] = "解析器繁忙"
                    }
                }
                : new JsonObject
                {
                    ["data"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["episodeId"] = "other",
                            ["downloadUrl"] = "https://cdn.example.com/wrong.mp4"
                        },
                        new JsonObject
                        {
                            ["episodeId"] = "vid-81",
                            ["downloadUrl"] = "https://cdn.example.com/81.mp4",
                            ["sizeBytes"] = 123L
                        }
                    }
                };
            return Task.FromResult<JsonNode?>(response);
        };
        service.DelayForTests = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        var playback = await service.GetVideoPlaybackAsync(
            new DramaSourceSettings(),
            HongguoHighCrypto.EncodeEpisodeId("book-1", 81, "vid-81"),
            "1080P",
            CancellationToken.None);

        playback.Url.Should().Be("https://cdn.example.com/81.mp4");
        playback.Size.Should().Be(123L);
        calls.Should().Be(2);
        delays.Should().Equal(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetVideoPlaybackAsync_Fails_After_Three_Empty_Responses()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var calls = 0;
        var delays = new List<TimeSpan>();
        service.AuthedRequestForTests = (_, _, _, _, _) =>
        {
            calls++;
            return Task.FromResult<JsonNode?>(new JsonArray
            {
                new JsonObject { ["episodeId"] = "vid-81", ["message"] = "暂无地址" }
            });
        };
        service.DelayForTests = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        var act = () => service.GetVideoPlaybackAsync(
            new DramaSourceSettings(),
            HongguoHighCrypto.EncodeEpisodeId("book-1", 81, "vid-81"),
            "1080P",
            CancellationToken.None);

        await act.Should().ThrowAsync<HongguoHighException>()
            .WithMessage("*连续 3 次*暂无地址*");
        calls.Should().Be(3);
        delays.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetVideoPlaybackAsync_Retries_Server_Missing_Address_Error()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var calls = 0;
        var delays = new List<TimeSpan>();
        service.AuthedRequestForTests = (_, path, _, _, _) =>
        {
            path.Should().Be("/video/batch-parse");
            calls++;
            if (calls == 1)
                throw new HongguoHighException("高码率解析未返回可用下载地址", 422);

            return Task.FromResult<JsonNode?>(new JsonArray
            {
                new JsonObject
                {
                    ["episodeId"] = "vid-32",
                    ["downloadUrl"] = "https://cdn.example.com/32.mp4"
                }
            });
        };
        service.DelayForTests = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        var playback = await service.GetVideoPlaybackAsync(
            new DramaSourceSettings(),
            HongguoHighCrypto.EncodeEpisodeId("book-1", 32, "vid-32"),
            "1080P",
            CancellationToken.None);

        playback.Url.Should().Be("https://cdn.example.com/32.mp4");
        calls.Should().Be(2);
        delays.Should().Equal(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetVideoPlaybackAsync_Does_Not_Retry_Authentication_Error()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var calls = 0;
        var delays = new List<TimeSpan>();
        service.AuthedRequestForTests = (_, _, _, _, _) =>
        {
            calls++;
            throw new HongguoHighException("token 已失效", 401);
        };
        service.DelayForTests = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        var act = () => service.GetVideoPlaybackAsync(
            new DramaSourceSettings(),
            HongguoHighCrypto.EncodeEpisodeId("book-1", 32, "vid-32"),
            "1080P",
            CancellationToken.None);

        await act.Should().ThrowAsync<HongguoHighException>()
            .WithMessage("token 已失效");
        calls.Should().Be(1);
        delays.Should().BeEmpty();
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

    [Fact]
    public async Task Calendar_List_Fetches_First_Page_Then_Uses_Bounded_Parallel_Batch()
    {
        var calls = new System.Collections.Concurrent.ConcurrentBag<int>();
        var active = 0;
        var maxActive = 0;
        var progress = new List<string>();
        var today = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        async Task<IReadOnlyList<DramaSearchItem>> Load(int page, CancellationToken cancellationToken)
        {
            calls.Add(page);
            var current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            try
            {
                await Task.Delay(20, cancellationToken);
                var count = page switch { 1 or 2 => 20, 3 => 1, _ => 0 };
                return Enumerable.Range(1, count)
                    .Select(index => new DramaSearchItem($"hghigh:{page}-{index}", $"剧{page}-{index}", "", 1, "", "", "作者", today))
                    .ToArray();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var items = await HongguoHighApiService.FetchCalendarListForTestsAsync(
            Load,
            days: 1,
            new InlineProgress<string>(progress.Add),
            CancellationToken.None);

        items.Should().HaveCount(41);
        calls.Should().Contain([1, 2, 3, 4]);
        maxActive.Should().Be(5);
        progress.Should().Contain(message => message.Contains("已发现 41 部", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Calendar_List_Reports_Completed_Pages_Without_Waiting_For_Slowest_Peer()
    {
        var releaseSlowPage = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var partialCounts = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var today = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        async Task<IReadOnlyList<DramaSearchItem>> Load(int page, CancellationToken cancellationToken)
        {
            if (page == 4)
                await releaseSlowPage.Task.WaitAsync(cancellationToken);
            var count = page == 4 ? 1 : 20;
            return Enumerable.Range(1, count)
                .Select(index => new DramaSearchItem(
                    $"hghigh:stream-{page}-{index}", $"剧{page}-{index}", "", 1, "", "", "作者", today))
                .ToArray();
        }

        var loading = HongguoHighApiService.FetchCalendarListForTestsAsync(
            Load,
            days: 1,
            progress: null,
            CancellationToken.None,
            new InlineProgress<IReadOnlyList<DramaSearchItem>>(items => partialCounts.Enqueue(items.Count)));

        await Task.Delay(150);
        partialCounts.Should().Contain([20, 40, 60]);
        loading.IsCompleted.Should().BeFalse();

        releaseSlowPage.SetResult(true);
        var items = await loading;
        items.Should().HaveCount(61);
        partialCounts.Should().Contain(61);
    }

    [Fact]
    public async Task Calendar_Details_Report_Incrementally_And_Reuse_BookInfo_Cache()
    {
        var handler = new HighDirectoryHandler();
        using var httpClient = new HttpClient(handler);
        var service = new HongguoHighApiService(httpClient);
        IReadOnlyList<DramaSearchItem>? reported = null;
        IReadOnlyList<DramaSearchItem> items =
        [
            new DramaSearchItem("hghigh:book-1", "剧1", "", 0, "", "", "", ""),
            new DramaSearchItem("hghigh:book-2", "剧2", "", 0, "", "", "", ""),
        ];

        var first = await service.EnrichNewReleaseItemsAsync(
            new DramaSourceSettings(),
            items,
            progress: null,
            CancellationToken.None,
            new InlineProgress<IReadOnlyList<DramaSearchItem>>(batch => reported = batch));

        first.Should().OnlyContain(item => item.Author == "作者甲");
        first.Should().OnlyContain(item => item.PosterUrl == "https://p3-novel.byteimg.com/origin/novel-pic/directory-cover");
        reported.Should().NotBeNull();
        reported!.Should().OnlyContain(item => item.Author == "作者甲");
        handler.RequestCount.Should().Be(2);

        await service.EnrichNewReleaseItemsAsync(
            new DramaSourceSettings(), items, progress: null, CancellationToken.None);
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task Ai_Landpage_Retries_504_With_Fresh_Signature()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var requestIds = new List<string>();
        var executeCount = 0;
        var delays = new List<TimeSpan>();
        service.AuthedRequestForTests = (_, _, spec, _, _) =>
        {
            requestIds.Add(spec["requestId"]!.GetValue<string>());
            return Task.FromResult<JsonNode?>(new JsonObject { ["host"] = "example.invalid", ["path"] = "/landpage" });
        };
        service.ExecuteSignedRequestForTests = (_, _, _, _) =>
        {
            executeCount++;
            if (executeCount == 1)
            {
                throw new HongguoHighException("HTTP 504", 504);
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        };
        service.DelayForTests = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        var payload = await service.FetchAiLandpagePageAsync(
            new DramaSourceSettings(),
            page: 1,
            timeoutSeconds: 30,
            CancellationToken.None);

        payload!["ok"]!.GetValue<bool>().Should().BeTrue();
        executeCount.Should().Be(2);
        requestIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        delays.Should().Equal(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task Calendar_List_Reuses_Five_Minute_Cache()
    {
        using var httpClient = new HttpClient(new FailAllHandler());
        var service = new HongguoHighApiService(httpClient);
        var calls = 0;
        var progress = new List<string>();
        var settings = new DramaSourceSettings { HghighAccount = "cache@example.com" };

        Task<IReadOnlyList<DramaSearchItem>> Load(int page, CancellationToken _)
        {
            calls++;
            IReadOnlyList<DramaSearchItem> result =
            [
                new DramaSearchItem(
                    $"hghigh:cache-{page}",
                    "缓存剧目",
                    "",
                    1,
                    "",
                    "",
                    "作者",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            ];
            return Task.FromResult(result);
        }

        var first = await service.GetCalendarNewForTestsAsync(
            "manju", settings, 1, false, new InlineProgress<string>(progress.Add), Load, CancellationToken.None);
        var second = await service.GetCalendarNewForTestsAsync(
            "manju", settings, 1, false, new InlineProgress<string>(progress.Add), Load, CancellationToken.None);

        first.Should().Equal(second);
        calls.Should().Be(1);
        progress.Should().Contain(message => message.Contains("5 分钟内上新列表缓存", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("16.7.19", true)]
    [InlineData("16.0.0", true)]
    [InlineData("17.17.0", false)]
    [InlineData("17.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Frida_16_Is_Usable_And_17_Is_Not(string? version, bool usable)
    {
        HongguoHighMasterProvisioner.IsUsableFridaVersion(version).Should().Be(usable);
    }

    [Fact]
    public void Provision_Script_Rejects_Frida_17()
    {
        var script = FindRepoFile("src", "ShortDrama", "ShortDrama.Infrastructure", "Tools", "hongguo-high", "provision_startup_masters.py");
        var text = File.ReadAllText(script);
        text.Should().Contain("startswith(\"17.\")");
        text.Should().Contain("16.7.19");
        text.Should().Contain("无法 import frida");
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class HighSearchAndDirectoryHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            string json;
            if (request.RequestUri.AbsolutePath.Contains("/directory/list/", StringComparison.Ordinal))
            {
                var episodes = new JsonArray(
                    Enumerable.Range(1, 58).Select(index => JsonValue.Create($"vid-{index}")).ToArray());
                json = new JsonObject
                {
                    ["code"] = 0,
                    ["data"] = new JsonObject { ["item_list"] = episodes },
                }.ToJsonString();
            }
            else
            {
                json = """
                    {"code":0,"data":{"search_data":[{"books":[{"book_id":"123456","book_name":"高码率剧","author":"甲","audio_thumb_uri":"https://cover","abstract":"简介","category":"漫剧","drama_chapter_number":60}]}]}}
                    """;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class HighDirectoryHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            RequestCount++;
            var json = """
                {"code":0,"data":{"item_list":["vid-a","vid-b"],"cover_bundle":{"url_list":["https://p3-novel.byteimg.com/origin/novel-pic/directory-cover"]},"book_info":{"book_name":"测试剧","author":"作者甲","chapter_number":2}}}
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
