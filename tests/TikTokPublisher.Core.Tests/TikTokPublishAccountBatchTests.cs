using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPublishAccountBatchTests
{
    [Fact]
    public void Scan_validates_nickname_biography_and_square_avatar()
    {
        using var workspace = new MaterialRoot();
        workspace.Add("AfterFive");
        workspace.Add("123");
        workspace.Add("SmallAvatar", imageSize: 450);
        workspace.Add("LongBiography", biography: new string('a', 161));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "MissingFiles"));

        var rows = workspace.Scan();
        Assert.Equal(5, rows.Count);
        Assert.Equal("AfterFive", Assert.Single(rows.Where(row => row.State == PublishAccountMaterialState.Ready)).Nickname);
        Assert.Equal(4, rows.Count(row => row.State == PublishAccountMaterialState.Invalid));
    }

    [Fact]
    public void Submission_requires_exclusive_reservation_and_receipt_skips_next_scan()
    {
        using var workspace = new MaterialRoot();
        var folder = workspace.Add("AfterFive");
        var material = Assert.Single(workspace.Scan());

        using (var reservation = TikTokPublishAccountMaterials.Reserve(material, "acct-one"))
        {
            Assert.Throws<IOException>(() => TikTokPublishAccountMaterials.Reserve(material, "acct-other"));
            Assert.False(File.Exists(Path.Combine(folder, TikTokPublishAccountMaterials.StatusFileName)));
            reservation.BeginSubmission();
            Assert.Equal(PublishAccountMaterialState.Pending, Assert.Single(workspace.Scan()).State);
            reservation.Complete(accepted: true, receiptId: "receipt-001");
        }

        Assert.Equal(PublishAccountMaterialState.Used, Assert.Single(workspace.Scan()).State);
        Assert.Throws<InvalidOperationException>(() => TikTokPublishAccountMaterials.Reserve(material, "acct-other"));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, TikTokPublishAccountMaterials.StatusFileName)));
        Assert.Equal("acct-one", document.RootElement.GetProperty("accountId").GetString());
        Assert.Equal("receipt-001", document.RootElement.GetProperty("receiptId").GetString());
    }

    [Fact]
    public void Only_explicit_rejection_releases_a_submitted_material_for_retry()
    {
        using var workspace = new MaterialRoot();
        workspace.Add("AfterFive");
        var material = Assert.Single(workspace.Scan());
        using (TikTokPublishAccountMaterials.Reserve(material, "acct-one"))
        {
            Assert.Equal(PublishAccountMaterialState.Ready, Assert.Single(workspace.Scan()).State);
        }
        using (var attempt = TikTokPublishAccountMaterials.Reserve(material, "acct-one"))
        {
            attempt.BeginSubmission();
            attempt.Complete(accepted: false, receiptId: "");
        }
        Assert.Equal(PublishAccountMaterialState.Ready, Assert.Single(workspace.Scan()).State);

        using (var attempt = TikTokPublishAccountMaterials.Reserve(material, "acct-one"))
        {
            attempt.BeginSubmission();
        }
        Assert.Equal(PublishAccountMaterialState.Pending, Assert.Single(workspace.Scan()).State);
        Assert.Throws<InvalidOperationException>(() => TikTokPublishAccountMaterials.Reserve(material, "acct-two"));
    }

    [Theory]
    [InlineData("domestic", "domestic=one")]
    [InlineData("international", "international=two")]
    public void Saved_publishing_cookies_stay_on_their_account_site(string site, string expected)
    {
        using var workspace = new MaterialRoot();
        var stateFile = Path.Combine(workspace.Path, "state.json");
        File.WriteAllText(stateFile, """
            {"cookies":[
              {"domain":"tiktokdramacenter.com","path":"/","name":"domestic","value":"one","expires":-1},
              {"domain":".tiktok.com","path":"/","name":"international","value":"two","expires":-1},
              {"domain":"www.tiktokdramacenter.com","path":"/","name":"expired","value":"old","expires":1},
              {"domain":"unrelated.tiktokdramacenter.com","path":"/","name":"unrelated","value":"no","expires":-1},
              {"domain":".tiktok.com","path":"/elsewhere","name":"scoped","value":"no","expires":-1}
            ]}
            """);

        Assert.Equal(expected, TikTokApiUploadService.ReadPublishingCookies(stateFile, site));
    }

    private const string ValidProfile = """
        {"code":0,"data":{"profile":{"isParent":true,"email":"test@example.test"},"institution":{"institutionId":"1234567890123456789","sourceV2":3}}}
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(401)]
    [InlineData(200)]
    public async Task Existing_login_is_reused_and_an_expired_login_is_renewed_only_once(int expiredStatus)
    {
        var expired = expiredStatus != 0;
        var calls = new List<string>();
        var profileReads = 0;
        var logins = 0;
        var marked = false;
        var material = new PublishAccountMaterial("", "AfterFive", "Short drama clips.", "",
            PublishAccountMaterialState.Ready, "");
        var result = await TikTokApiUploadService.SubmitPublishingAccountAsync(
            material,
            (path, body, _) =>
            {
                calls.Add(path.Split('/').Last());
                if (path.EndsWith("/profile"))
                    return Task.FromResult(expired && ++profileReads == 1
                        ? new TikTokApiUploadService.FetchReply(expiredStatus, """{"statusCode":401}""")
                        : new TikTokApiUploadService.FetchReply(200, ValidProfile));
                if (path.EndsWith("/detail"))
                    return Task.FromResult(new TikTokApiUploadService.FetchReply(200,
                        """{"code":0,"data":{"isRegistering":false,"creatorHostingConfig":{"enabled":false}}}"""));
                Assert.True(marked);
                using var json = JsonDocument.Parse(body!);
                var submitted = json.RootElement;
                Assert.Equal("1234567890123456789", submitted.GetProperty("institutionID").GetString());
                Assert.Equal(material.Nickname, submitted.GetProperty("uniqueName").GetString());
                Assert.Equal(material.Biography, submitted.GetProperty("description").GetString());
                Assert.Equal("avatar-uploaded", submitted.GetProperty("avatarUri").GetString());
                Assert.True(submitted.GetProperty("enabled").GetBoolean());
                return Task.FromResult(new TikTokApiUploadService.FetchReply(200,
                    """{"code":0,"data":{"requestId":"receipt-1"}}"""));
            },
            _ =>
            {
                logins++;
                return Task.CompletedTask;
            },
            _ =>
            {
                Assert.False(marked);
                calls.Add("avatar");
                return Task.FromResult("avatar-uploaded");
            },
            () => marked = true,
            progress: null,
            CancellationToken.None);

        Assert.Equal(expired ? 1 : 0, logins);
        Assert.Equal(expired ? ["profile", "profile", "detail", "avatar", "creator-hosting"]
            : new[] { "profile", "detail", "avatar", "creator-hosting" }, calls);
        Assert.Equal(PublishAccountCreationState.Accepted, result.State);
        Assert.Equal("receipt-1", result.ReceiptId);
    }

    [Theory]
    [InlineData("""{"code":0,"data":{"isRegistering":true}}""")]
    [InlineData("""{"code":0,"data":{"creatorHostingConfig":{"enabled":true}}}""")]
    public async Task An_existing_platform_request_keeps_a_new_material_unsubmitted(string detail)
    {
        using var workspace = new MaterialRoot();
        workspace.Add("AfterFive");
        var material = Assert.Single(workspace.Scan());
        using var reservation = TikTokPublishAccountMaterials.Reserve(material, "account");

        await Assert.ThrowsAsync<InvalidOperationException>(() => TikTokApiUploadService.SubmitPublishingAccountAsync(
            material,
            (path, _, _) => Task.FromResult(path.EndsWith("/profile")
                ? new TikTokApiUploadService.FetchReply(200, ValidProfile)
                : new TikTokApiUploadService.FetchReply(200, detail)),
            _ => throw new Xunit.Sdk.XunitException("Unexpected login."),
            _ => throw new Xunit.Sdk.XunitException("Unexpected avatar upload."),
            reservation.BeginSubmission,
            progress: null,
            CancellationToken.None));

        Assert.False(reservation.SubmissionStarted);
        Assert.Equal(PublishAccountMaterialState.Ready, Assert.Single(workspace.Scan()).State);
    }

    [Theory]
    [InlineData(403, 0)]
    [InlineData(429, 0)]
    [InlineData(503, 0)]
    [InlineData(401, 1)]
    public async Task Profile_validation_failure_does_not_upload_or_submit(int status, int expectedLoginCount)
    {
        var logins = 0;
        var material = new PublishAccountMaterial("", "AfterFive", "Description.", "",
            PublishAccountMaterialState.Ready, "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => TikTokApiUploadService.SubmitPublishingAccountAsync(
            material,
            (_, _, _) => Task.FromResult(new TikTokApiUploadService.FetchReply(status, "")),
            _ =>
            {
                logins++;
                return Task.CompletedTask;
            },
            _ => throw new Xunit.Sdk.XunitException("Unexpected avatar upload."),
            () => Assert.Fail("Unexpected submission."),
            progress: null,
            CancellationToken.None));
        Assert.Equal(expectedLoginCount, logins);
    }

    [Fact]
    public void Publishing_avatar_uses_its_own_image_service_for_apply_and_commit()
    {
        const string imageHost = "https://image.example.test";
        var serviceId = TikTokApiUploadService.PublishingAvatarServiceId;
        var apply = TikTokApiUploadService.BuildImageApplyUri(imageHost, 500, "nonce", serviceId);
        var commit = TikTokApiUploadService.BuildImageCommitUri(imageHost, serviceId);
        Assert.Contains("ServiceId=fjfgkmyddx", apply.Query);
        Assert.Contains("ServiceId=fjfgkmyddx", commit.Query);
        Assert.Contains("ServiceId=xbj99bix8q", TikTokApiUploadService.BuildImageCommitUri(imageHost).Query);
    }

    [Fact]
    public async Task Unknown_submission_response_is_recorded_as_pending_after_one_attempt()
    {
        var submissions = 0;
        using var workspace = new MaterialRoot();
        workspace.Add("AfterFive");
        var material = Assert.Single(workspace.Scan());
        using var reservation = TikTokPublishAccountMaterials.Reserve(material, "account");
        var result = await TikTokApiUploadService.SubmitPublishingAccountAsync(
            material,
            (path, _, _) =>
            {
                if (path.EndsWith("/profile"))
                    return Task.FromResult(new TikTokApiUploadService.FetchReply(200, ValidProfile));
                if (path.EndsWith("/detail"))
                    return Task.FromResult(new TikTokApiUploadService.FetchReply(200, """{"code":0,"data":{}}"""));
                Assert.True(reservation.SubmissionStarted);
                submissions++;
                return Task.FromResult(new TikTokApiUploadService.FetchReply(503, ""));
            },
            _ => throw new InvalidOperationException("Unexpected login."),
            _ => Task.FromResult("avatar-uploaded"),
            reservation.BeginSubmission,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, submissions);
        Assert.Equal(PublishAccountCreationState.Pending, result.State);
        Assert.Equal(PublishAccountMaterialState.Pending, Assert.Single(workspace.Scan()).State);
    }

    [Theory]
    [InlineData("""{"code":0,"data":{"request_id":"receipt-001"}}""", 0, "receipt-001")]
    [InlineData("""{"code":0}""", 0, "")]
    [InlineData("""{"code":4501}""", 1, "")]
    [InlineData("""{"message":"ok"}""", 2, "")]
    public void Only_explicit_platform_success_yields_an_acceptance(string body, int state, string receipt)
    {
        var result = TikTokApiUploadService.ParsePublishingSubmission(body);
        Assert.Equal((PublishAccountCreationState)state, result.State);
        Assert.Equal(receipt, result.ReceiptId);
    }

    private sealed class MaterialRoot : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tiktok-publish-account-");
        public string Path => _directory.FullName;

        public string Add(string nickname, string biography = "Short drama highlights and original stories.", int imageSize = 500)
        {
            var path = Directory.CreateDirectory(System.IO.Path.Combine(Path, nickname)).FullName;
            File.WriteAllText(System.IO.Path.Combine(path, "bio.txt"), biography);
            using var image = new Image<Rgba32>(imageSize, imageSize);
            image.SaveAsPng(System.IO.Path.Combine(path, "avatar.png"));
            return path;
        }

        public IReadOnlyList<PublishAccountMaterial> Scan() => TikTokPublishAccountMaterials.Scan(Path, CancellationToken.None);
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
