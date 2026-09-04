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
    public async Task Standard_Playback_Uses_Preload_Signing_And_Decodes_MainUrl()
    {
        using var http = new HttpClient();
        var service = new HongguoHighApiService(http);
        JsonObject? captured = null;
        service.AuthedRequestForTests = (_, path, data, _, _) =>
        {
            path.Should().Be("/redguo/sign");
            captured = data.DeepClone().AsObject();
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
                            ["spade_a"] = "decrypt-material-low",
                            ["gear_des_key"] = "0:MP4|1:encrypt|4:360p|5:normal"
                        },
                        new JsonObject
                        {
                            ["video_id"] = "video-1",
                            ["main_url"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://cdn.example/video.mp4")),
                            ["spade_a"] = "decrypt-material",
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

        captured!["path"]!.GetValue<string>().Should().Be("/novel/player/multi_video_model/v1/");
        captured["method"]!.GetValue<string>().Should().Be("POST");
        captured["json"]!["mixed_video_id_map"]!["1"]![0]!.GetValue<string>().Should().Be("video-1");
        playback.Url.Should().Be("https://cdn.example/video.mp4");
        playback.SpadeA.Should().Be("decrypt-material");
        playback.EncryptedUrls.Should().Contain("https://cdn.example/video.mp4");
    }
}
