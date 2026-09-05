using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoStandardEditionTests
{
    [Fact]
    public void Profile_Uses_Independent_Standard_Identity()
    {
        var profile = HongguoClientProfile.Standard;

        profile.AppId.Should().Be("hongguo_desktop");
        profile.ApiBase.Should().Be("https://m.iusc.cc/api/hongguo/client/v1");
        profile.ClientVersion.Should().Be("2.1.7");
        profile.RegistryKey.Should().Be(@"Software\HongguoDownloader");
        profile.UsesServerBatchPlayback.Should().BeFalse();
        HongguoClientProfile.NormalizeEdition(null).Should().Be("high");
        HongguoClientProfile.NormalizeEdition("unexpected").Should().Be("high");
    }

    [Fact]
    public void Startup_Envelope_Uses_Selected_Profile()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var device = new HongguoHighDevice("device-standard", key);
        var inner = HongguoHighCrypto.BuildStartupInner(
            device,
            "/auth/login",
            new JsonObject { ["email"] = "a@example.test" },
            "proof",
            HongguoClientProfile.Standard);
        var envelope = HongguoHighCrypto.BuildStartupEnvelope(
            inner,
            "POST",
            "/auth/login",
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            HongguoClientProfile.Standard);

        inner["app_id"]!.GetValue<string>().Should().Be("hongguo_desktop");
        inner["version"]!.GetValue<string>().Should().Be("2.1.7");
        envelope["app_id"]!.GetValue<string>().Should().Be("hongguo_desktop");
        envelope["version"]!.GetValue<string>().Should().Be("2.1.7");
    }

    [Fact]
    public void Startup_Masters_Are_Shared_By_Edition()
    {
        var root = Path.Combine(Path.GetTempPath(), "hongguo-editions-" + Guid.NewGuid().ToString("N"));
        HongguoHighDeviceStore.CacheDirectoryOverride = root;
        try
        {
            var standardMaster = HongguoHighCrypto.ToBase64Url(Enumerable.Repeat((byte)2, 48).ToArray());
            HongguoHighDeviceStore.CacheStartupMasters(standardMaster, standardMaster, "standard-device", HongguoClientProfile.Standard);

            HongguoHighDeviceStore.LoadStartupMastersRaw(HongguoClientProfile.Standard).Enc.Should().Be(standardMaster);
            HongguoHighDeviceStore.LoadStartupMastersRaw(HongguoClientProfile.High).Enc.Should().Be(standardMaster);
            HongguoHighDeviceStore.GetMastersCachePath(HongguoClientProfile.Standard)
                .Should().Be(HongguoHighDeviceStore.GetMastersCachePath(HongguoClientProfile.High));
        }
        finally
        {
            HongguoHighDeviceStore.CacheDirectoryOverride = null;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Standard_Playback_Uses_Detail_Api_And_Prefers_Plain_DirectUrl()
    {
        using var http = new HttpClient();
        var service = new HongguoHighApiService(http);
        var captured = new List<JsonObject>();
        service.AuthedRequestForTests = (_, path, data, _, _) =>
        {
            path.Should().Be("/redguo/sign");
            captured.Add(data.DeepClone().AsObject());
            return Task.FromResult<JsonNode?>(new JsonObject { ["descriptor"] = true });
        };
        service.ExecuteSignedRequestForTests = (_, _, _, _) => Task.FromResult<JsonNode?>(
            new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["video_list"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["video_id"] = "video-1",
                            ["main_url"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://cdn.example/video-360.mp4")),
                            ["url_info"] = new JsonObject
                            {
                                ["main_url_direct_url"] = "https://cdn.example/video-360-direct.mp4"
                            },
                            ["spade_a"] = "decrypt-material-low",
                            ["gear_des_key"] = "0:MP4|1:encrypt|4:360p|5:normal"
                        },
                        new JsonObject
                        {
                            ["video_id"] = "video-1",
                            ["main_url"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://cdn.example/video.mp4")),
                            ["url_info"] = new JsonObject
                            {
                                ["main_url_direct_url"] = "https://cdn.example/video-direct.mp4"
                            },
                            ["spade_a"] = "decrypt-material",
                            ["encrypt"] = false,
                            ["size"] = 123456L,
                            ["gear_des_key"] = "0:MP4|1:encrypt|4:1080p|5:normal"
                        }
                    }
                }
            });

        var playback = await service.GetVideoPlaybackAsync(
            new DramaSourceSettings { HghighEdition = "standard" },
            HongguoHighCrypto.EncodeEpisodeId("book-1", 1, "video-1"),
            "1080P",
            CancellationToken.None);

        var detailRequest = captured.Single(item => item["purpose"]!.GetValue<string>() == "multi_video_detail");
        var modelRequest = captured.Single(item => item["purpose"]!.GetValue<string>() == "multi_video_model");
        detailRequest["path"]!.GetValue<string>().Should().Be("/novel/player/multi_video_detail/v1/");
        detailRequest["method"]!.GetValue<string>().Should().Be("POST");
        detailRequest["json"]!["series_id"]!.GetValue<string>().Should().Be("book-1");
        detailRequest["json"]!["biz_param"]!["caller_scene"]!.GetValue<string>().Should().Be("three_col");
        detailRequest["json"]!["biz_param"]!["source"]!.GetValue<int>().Should().Be(7);
        detailRequest["json"]!["biz_param"]!["need_all_video_definition"]!.GetValue<bool>().Should().BeFalse();
        modelRequest["path"]!.GetValue<string>().Should().Be("/novel/player/multi_video_model/v1/");
        modelRequest["json"]!["mixed_video_id_map"]!["1"]!.AsArray()
            .Should().ContainSingle().Which.GetValue<string>().Should().Be("video-1");
        playback.Url.Should().Be("https://cdn.example/video-direct.mp4");
        playback.Size.Should().Be(123456L);
        playback.SpadeA.Should().BeEmpty();
        playback.EncryptedUrls.Should().BeEmpty();
        playback.Encrypted.Should().BeFalse();
    }

    [Fact]
    public async Task Standard_Playback_Skips_Batch_Plan_And_Uses_One_Model_Id_Per_Request()
    {
        using var http = new HttpClient();
        var service = new HongguoHighApiService(http);
        var calls = 0;
        service.AuthedRequestForTests = (_, _, data, _, _) =>
        {
            Interlocked.Increment(ref calls);
            var purpose = data["purpose"]!.GetValue<string>();
            if (purpose == "multi_video_detail")
                data["json"]!["series_id"]!.GetValue<string>().Should().Be("book-1");
            else
                data["json"]!["mixed_video_id_map"]!["1"]!.AsArray().Should().ContainSingle();
            return Task.FromResult<JsonNode?>(new JsonObject { ["descriptor"] = true });
        };
        service.ExecuteSignedRequestForTests = (_, _, _, _) => Task.FromResult<JsonNode?>(
            new JsonObject
            {
                ["video_list"] = new JsonArray(
                    Enumerable.Range(1, 3).Select(index => (JsonNode)new JsonObject
                    {
                        ["video_id"] = $"video-{index}",
                        ["main_url_direct_url"] = $"https://cdn.example/video-{index}.mp4",
                        ["gear_des_key"] = "0:MP4|4:1080p",
                    }).ToArray())
            });
        var settings = new DramaSourceSettings { HghighEdition = "standard" };
        var encoded = Enumerable.Range(1, 3)
            .Select(index => HongguoHighCrypto.EncodeEpisodeId("book-1", index, $"video-{index}"))
            .ToArray();

        using var plan = service.RegisterBatchParsePlan(settings, encoded, "1080P", 3);
        var playback = await Task.WhenAll(encoded.Select(id =>
            service.GetVideoPlaybackAsync(settings, id, "1080P", CancellationToken.None)));

        calls.Should().Be(6, "standard edition should use one detail and one single-id model request per episode");
        playback.Select(item => item.Url).Should().Equal(
            "https://cdn.example/video-1.mp4",
            "https://cdn.example/video-2.mp4",
            "https://cdn.example/video-3.mp4");
        playback.Should().OnlyContain(item => !item.Encrypted && item.EncryptedUrls.Count == 0);
    }
}
