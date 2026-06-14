using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Workflow;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Workflow;

public sealed class MaterialValidationServiceTests
{
    [Fact]
    public async Task ValidateAsync_Should_Mark_Missing_Videos_Directory_As_AutoFixable()
    {
        var workflowDir = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllTextAsync(Path.Combine(workflowDir, "短剧信息.txt"), """
原剧名: 亲戚别来我家
新剧名: 断亲之后我掀桌改命
时长: 43 分钟
集数: 41
成本: 6 万元
制作公司: 武汉云起漫影科技有限公司
""");

        var service = new MaterialValidationService(new TxtProjectInfoParser(), new StubProcessRunner());

        var result = await service.ValidateAsync(workflowDir, CancellationToken.None);

        result.Issues.Should().ContainSingle(issue =>
            issue.Code == "videos-dir-missing" &&
            issue.CanAutoFix);
    }

    [Fact]
    public async Task ValidateAsync_Should_Mark_Invalid_Info_As_AutoFixable()
    {
        var workflowDir = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(workflowDir, "videos"));
        await File.WriteAllTextAsync(Path.Combine(workflowDir, "短剧信息.txt"), """
原剧名: 亲戚别来我家
新剧名: 
""");

        var service = new MaterialValidationService(new TxtProjectInfoParser(), new StubProcessRunner());

        var result = await service.ValidateAsync(workflowDir, CancellationToken.None);

        result.Issues.Should().ContainSingle(issue =>
            issue.Code == "info-invalid" &&
            issue.CanAutoFix);
    }

    [Fact]
    public async Task ValidateAsync_Should_Mark_Unreadable_Bitrate_As_AutoFixable()
    {
        var workflowDir = Directory.CreateTempSubdirectory().FullName;
        var videosDir = Directory.CreateDirectory(Path.Combine(workflowDir, "videos")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workflowDir, "短剧信息.txt"), """
原剧名: 亲戚别来我家
新剧名: 断亲之后我掀桌改命
时长: 43 分钟
集数: 1
成本: 6 万元
制作公司: 武汉云起漫影科技有限公司
""");
        await File.WriteAllBytesAsync(Path.Combine(videosDir, "断亲之后我掀桌改命-第1集.mp4"), [1, 2, 3]);

        var service = new MaterialValidationService(new TxtProjectInfoParser(), new StubProcessRunner(exitCode: 1));

        var result = await service.ValidateAsync(workflowDir, CancellationToken.None);

        result.Issues.Should().ContainSingle(issue =>
            issue.Code == "video-bitrate-unreadable" &&
            issue.CanAutoFix);
    }

    private sealed class StubProcessRunner : IExternalProcessRunner
    {
        private readonly int _exitCode;

        public StubProcessRunner(int exitCode = 0)
        {
            _exitCode = exitCode;
        }

        public Task<ExternalProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ExternalProcessResult(_exitCode, string.Empty, string.Empty));
        }
    }
}
