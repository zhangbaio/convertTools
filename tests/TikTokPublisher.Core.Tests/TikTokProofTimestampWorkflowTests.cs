using FluentAssertions;
using PdfSharp.Pdf.IO;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProofTimestampWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proof_material_generates_timestamp_only_when_the_account_enables_it(bool enabled)
    {
        var workspace = Directory.CreateTempSubdirectory("proof-timestamp-");
        try
        {
            var item = new QueueProjectItem
            {
                ProjectDir = workspace.CreateSubdirectory("source").FullName,
                NewTitle = "测试剧名",
            };
            var account = AccountWithTimestamp(enabled);
            var path = TikTokTimestampCertificateService.GetOutputPath(item);
            var settings = new ClientSettings();
            await TikTokProofMaterialService.GenerateAsync(
                item, settings, account, forceRerun: false, log: null, CancellationToken.None);

            File.Exists(path).Should().Be(enabled);
            if (!enabled)
                return;

            using (var certificate = PdfReader.Open(path, PdfDocumentOpenMode.Import))
                certificate.PageCount.Should().Be(2);
            var generated = File.ReadAllBytes(path);
            await TikTokProofMaterialService.GenerateAsync(
                item, settings, account, forceRerun: false, log: null, CancellationToken.None);
            File.ReadAllBytes(path).Should().Equal(generated);

            await TikTokProofMaterialService.GenerateAsync(
                item, settings, AccountWithTimestamp(false), forceRerun: true, log: null, CancellationToken.None);
            File.ReadAllBytes(path).Should().Equal(generated);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Upload_only_requires_existing_certificate_and_never_creates_one()
    {
        var workspace = Directory.CreateTempSubdirectory("upload-timestamp-");
        try
        {
            var item = new QueueProjectItem
            {
                ProjectDir = workspace.CreateSubdirectory("source").FullName,
            };
            var account = AccountWithTimestamp(true);
            var path = TikTokTimestampCertificateService.GetOutputPath(item);
            var attempt = () => TikTokProofMaterialService.EnsureCurrentForUploadAsync(
                item, account, log: null, CancellationToken.None);

            await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*请先执行「证明材料」*");
            File.Exists(path).Should().BeFalse();

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var damaged = "%PDF-1.7\n"u8.ToArray().Concat(new byte[128]).ToArray();
            File.WriteAllBytes(path, damaged);
            await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*请先执行「证明材料」*");
            File.ReadAllBytes(path).Should().Equal(damaged);

            using (var document = new PdfSharp.Pdf.PdfDocument())
            {
                document.AddPage();
                document.Save(path);
            }
            var original = File.ReadAllBytes(path);
            (await attempt()).Should().BeEmpty();
            TikTokMaterialValidationService.ValidateGeneratedUploadMaterials(
                item.ProjectDir, account, new TikTokMaterialValidationService.Options(), log: null);
            File.ReadAllBytes(path).Should().Equal(original);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            workspace.Delete(recursive: true);
        }
    }

    private static TikTokAccountProfile AccountWithTimestamp(bool enabled) => new()
    {
        TiktokCopyrightMaterialTypes = enabled
            ? [TikTokPublishConstants.FilingOrDistributionLicenseMaterialType]
            : ["work_registration_certificate"],
        TiktokProofDeclarantCompanyName = "测试主体",
    };
}
