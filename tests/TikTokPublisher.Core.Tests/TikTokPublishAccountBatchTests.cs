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
    [InlineData("tiktokdramacenter.com", ".tiktokdramacenter.com")]
    [InlineData("www.tiktokdramacenter.com", "www.tiktokdramacenter.com")]
    [InlineData(".tiktokdramacenter.com", ".tiktokdramacenter.com")]
    [InlineData("drama.tiktok.com", "drama.tiktok.com")]
    public void Dedicated_publishing_page_imports_the_account_cookie_scope(string domain, string expected)
    {
        Assert.Equal(expected, TikTokPublishAccountBatchRunner.PublishingCookieDomain(domain));
    }

    [Theory]
    [InlineData("""{"enabled":true,"uniqueName":"AfterFive"}""", true)]
    [InlineData("""{"enabled":true,"unique_name":"AfterFive"}""", true)]
    [InlineData("""{"enabled":true,"uniqueName":"WrongAccount"}""", false)]
    [InlineData("""{"enabled":false,"uniqueName":"AfterFive"}""", false)]
    [InlineData("not json", false)]
    public void Submission_response_is_correlated_with_the_expected_material(string body, bool expected)
    {
        Assert.Equal(expected, TikTokPublishAccountBatchRunner.MatchesSubmissionBody(body, "AfterFive"));
    }

    [Theory]
    [InlineData("""{"code":0,"data":{"request_id":"receipt-001"}}""", 0, "receipt-001")]
    [InlineData("""{"code":0}""", 0, "")]
    [InlineData("""{"code":4501}""", 1, "")]
    [InlineData("""{"message":"ok"}""", 2, "")]
    public void Only_explicit_platform_success_yields_an_acceptance(string body, int state, string receipt)
    {
        var result = TikTokPublishAccountBatchRunner.ParseSubmission(body);
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
