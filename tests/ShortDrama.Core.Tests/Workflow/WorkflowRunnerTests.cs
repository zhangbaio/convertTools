using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Workflow;
using Xunit;

namespace ShortDrama.Core.Tests.Workflow;

public sealed class WorkflowRunnerTests
{
    [Fact]
    public async Task RunAsync_Should_Execute_CostReport_Step()
    {
        var builder = new FakeCostReportBuilder();
        var batchFileRenamer = new FakeBatchFileRenamer();
        var posterRenamer = new FakePosterRenamer();
        var projectInfoRewriter = new FakeProjectInfoRewriter();
        var projectImageGenerator = new FakeProjectImageGenerator();
        var videoTranscoder = new FakeVideoTranscoder();
        var videoMaterialConverter = new FakeVideoMaterialConverter();
        var dramaDownloader = new FakeDramaDownloader();
        var weixinChannelUploader = new FakeWeixinChannelUploader();
        var runner = new WorkflowRunner(
            builder,
            batchFileRenamer,
            posterRenamer,
            projectInfoRewriter,
            projectImageGenerator,
            videoTranscoder,
            videoMaterialConverter,
            dramaDownloader,
            weixinChannelUploader);

        var definition = new WorkflowDefinition(
            ProjectKey: "project-1",
            DisplayName: "测试项目",
            ProjectDir: "/tmp/project",
            ConfigDir: "/tmp/config",
            Steps:
            [
                new WorkflowStep(
                    Type: "cost-report",
                    Template: "/tmp/template.docx",
                    ConfigFile: null,
                    OutputFile: null,
                    InputDir: null,
                    OutputDir: "/tmp/output",
                    NameTemplate: null,
                    Retry: 0)
            ]);

        var result = await runner.RunAsync(definition, progress: null, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Steps.Should().HaveCount(1);
        result.Steps[0].Ok.Should().BeTrue();
        result.Steps[0].Outputs.Should().NotBeNull();
        result.Steps[0].Outputs!["png"].Should().Be("/tmp/output/成本报表.png");
        result.Steps[0].Outputs!["docx"].Should().Be("/tmp/output/成本报表原文件.docx");
        builder.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_Should_Execute_BatchFileRename_Step()
    {
        var builder = new FakeCostReportBuilder();
        var batchFileRenamer = new FakeBatchFileRenamer();
        var posterRenamer = new FakePosterRenamer();
        var projectInfoRewriter = new FakeProjectInfoRewriter();
        var projectImageGenerator = new FakeProjectImageGenerator();
        var videoTranscoder = new FakeVideoTranscoder();
        var videoMaterialConverter = new FakeVideoMaterialConverter();
        var dramaDownloader = new FakeDramaDownloader();
        var weixinChannelUploader = new FakeWeixinChannelUploader();
        var runner = new WorkflowRunner(
            builder,
            batchFileRenamer,
            posterRenamer,
            projectInfoRewriter,
            projectImageGenerator,
            videoTranscoder,
            videoMaterialConverter,
            dramaDownloader,
            weixinChannelUploader);

        var definition = new WorkflowDefinition(
            ProjectKey: "project-1",
            DisplayName: "测试项目",
            ProjectDir: "/tmp/project",
            ConfigDir: "/tmp/config",
            Steps:
            [
                new WorkflowStep(
                    Type: "batch-file-rename",
                    Template: null,
                    ConfigFile: "/tmp/config.txt",
                    OutputFile: null,
                    InputDir: "/tmp/project/videos",
                    OutputDir: null,
                    NameTemplate: "{name}-第{index}集",
                    Retry: 0)
            ]);

        var result = await runner.RunAsync(definition, progress: null, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Steps.Should().HaveCount(1);
        result.Steps[0].Ok.Should().BeTrue();
        result.Steps[0].Outputs!["renamedCount"].Should().Be("1");
        result.Steps[0].Outputs!["file1"].Should().Be("/tmp/project/videos/新剧名-第1集.mp4");
    }

    private sealed class FakeVideoTranscoder : IVideoTranscoder
    {
        public Task<VideoTranscodeResult> TranscodeAsync(
            VideoTranscodeRequest request,
            IProgress<VideoTranscodeProgress>? progress,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new VideoTranscodeResult(
                TotalFiles: 0,
                TranscodedFiles: 0,
                SkippedFiles: 0,
                FailedFiles: 0,
                Outputs: [],
                Failures: []));
        }
    }

    private sealed class FakeDramaDownloader : IDramaDownloader
    {
        public Task<DramaDownloadResult> DownloadAsync(
            DramaDownloadRequest request,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DramaDownloadResult(
                Ok: true,
                OutputDir: request.OutputDir,
                VideoCount: 1,
                Message: "downloaded"));
        }
    }

    private sealed class FakeVideoMaterialConverter : IVideoMaterialConverter
    {
        public Task<VideoMaterialConvertResult> ConvertAsync(
            VideoMaterialConvertRequest request,
            IProgress<VideoMaterialConvertProgress>? progress,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new VideoMaterialConvertResult(
                TotalFiles: 0,
                ConvertedFiles: 0,
                SkippedFiles: 0,
                FailedFiles: 0,
                Outputs: [],
                Failures: []));
        }
    }

    private sealed class FakeWeixinChannelUploader : IWeixinChannelUploader
    {
        public Task<WeixinUploadResult> UploadAsync(
            WeixinUploadRequest request,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WeixinUploadResult(
                Ok: true,
                ProjectDir: request.ProjectDir,
                ConfigPath: request.ConfigPath,
                Message: "uploaded"));
        }
    }

    private sealed class FakeProjectImageGenerator : IProjectImageGenerator
    {
        public Task<ProjectImageGenerateResult> GenerateAsync(
            ProjectImageGenerateRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProjectImageGenerateResult(
                0,
                []));
        }
    }

    private sealed class FakeProjectInfoRewriter : IProjectInfoRewriter
    {
        public Task<ProjectInfoRewriteResult> RewriteAsync(
            ProjectInfoRewriteRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProjectInfoRewriteResult(
                request.OutputFilePath,
                "新剧名",
                "推荐语",
                "简介",
                "短标题",
                "#标签1#标签2"));
        }
    }

    private sealed class FakePosterRenamer : IPosterRenamer
    {
        public Task<PosterRenameResult> RenameAsync(
            PosterRenameRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PosterRenameResult(
                request.InputFilePath ?? "/tmp/海报图片.jpg",
                request.OutputFilePath ?? "/tmp/新剧名-海报.jpg",
                "新剧名"));
        }
    }

    private sealed class FakeBatchFileRenamer : IBatchFileRenamer
    {
        public Task<BatchFileRenameResult> RenameAsync(
            BatchFileRenameRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new BatchFileRenameResult(
                1,
                [
                    new BatchFileRenameItem(
                        "/tmp/project/videos/a.mp4",
                        "/tmp/project/videos/新剧名-第1集.mp4")
                ]));
        }
    }

    private sealed class FakeCostReportBuilder : ICostReportBuilder
    {
        public int CallCount { get; private set; }

        public Task<CostReportBuildResult> BuildAsync(
            string projectDir,
            string templateDocxPath,
            string? configDir,
            string outputDir,
            CancellationToken cancellationToken)
        {
            CallCount++;

            var project = new ProjectInfo(
                OriginalTitle: "原剧名",
                Title: "新剧名",
                Tagline: null,
                Synopsis: null,
                ShortTitle: null,
                Tags: null,
                EpisodeCount: 3,
                TotalMinutes: 8,
                CostAmountWan: 1,
                CompanyName: "测试公司",
                ProjectDir: projectDir,
                SourceFilePath: Path.Combine(projectDir, "短剧信息.txt"));

            return Task.FromResult(new CostReportBuildResult(
                PngPath: Path.Combine(outputDir, "成本报表.png"),
                DocxPath: Path.Combine(outputDir, "成本报表原文件.docx"),
                Project: project));
        }
    }
}
