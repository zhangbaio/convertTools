using PdfSharp.Pdf.IO;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Weixin.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinProofArtifactsServiceTests
{
    [Fact]
    public async Task Timestamp_certificate_uses_the_workflow_resolved_by_the_video_channel()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var workflow = root.CreateSubdirectory("workflow").FullName;
            var source = root.CreateSubdirectory("source").FullName;
            await File.WriteAllTextAsync(Path.Combine(workflow, "短剧信息.txt"), "新剧名：测试短剧\n制作公司：测试公司");
            var service = new WeixinProofArtifactsService(new WorkService(Path.Combine(workflow, "weixin-channel.json")));
            var job = new PublishJob { ProjectDirectory = source, ProjectName = "原剧名" };

            var path = await service.GenerateTimestampCertificateAsync(job, null, false, null, CancellationToken.None);

            Assert.Equal(Path.Combine(workflow, "可信时间戳认证证书.pdf"), path);
            Assert.Empty(Directory.GetFiles(source));
            using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
            {
                Assert.Equal(2, document.PageCount);
            }

            var lastWrite = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, lastWrite);
            Assert.Equal(path, await service.GenerateTimestampCertificateAsync(job, null, false, null, CancellationToken.None));
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class WorkService(string configPath) : IWorkService
    {
        public Task<string> EnsureWeixinUploadConfigAsync(string sourceProjectDir, string? backupRootDir,
            CancellationToken cancellationToken) => Task.FromResult(configPath);

        public Task<WorkRunResult> RunAsync(string rootDir, string? backupRootDir, bool force,
            IProgress<WorkRunEvent>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProjectWorkResult> RunProjectAsync(string sourceProjectDir, string? backupRootDir, bool force,
            IProgress<WorkRunEvent>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProjectWorkResult> RunProjectStepAsync(string sourceProjectDir, string? backupRootDir, string stepType,
            bool force, IProgress<WorkRunEvent>? progress, CancellationToken cancellationToken, string? configOverridePath = null) =>
            throw new NotSupportedException();

        public Task<ProjectTitleUpdateResult> UpdateProjectTitleAsync(string sourceProjectDir, string? backupRootDir,
            string newTitle, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RefreshWeixinConfigsAsync(string sourceProjectDir, string? backupRootDir,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProjectInfoAutoFillResult> AutoFillProjectInfoAsync(string sourceProjectDir, string? backupRootDir,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UploadRemuxResult> RemuxUploadVideosAsync(string sourceProjectDir, string? backupRootDir,
            IProgress<WorkRunEvent>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
