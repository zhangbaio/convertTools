using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Office;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Workflow;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Workflow;

public sealed class CostReportBuilderTests
{
    [Fact]
    public async Task BuildAsync_Should_Render_Png_Without_Template_When_OriginalDocx_Not_Requested()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var projectDir = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var configDir = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        var outputDir = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;

        await WriteProjectInfoAsync(projectDir);
        await WriteAssetImagesAsync(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, "config.txt"), """
CompanyName=测试公司
""");

        var builder = CreateBuilder();

        var result = await builder.BuildAsync(
            projectDir,
            templateDocxPath: Path.Combine(root, "missing-template.docx"),
            configDir,
            outputDir,
            CancellationToken.None);

        result.PngPath.Should().Be(Path.Combine(outputDir, "成本报表.png"));
        File.Exists(result.PngPath).Should().BeTrue();
        result.DocxPath.Should().BeEmpty();
        result.Project.CompanyName.Should().Be("测试公司");
    }

    [Fact]
    public async Task BuildAsync_Should_Keep_OriginalDocx_When_Config_Requests_It()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var projectDir = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var configDir = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        var outputDir = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;

        await WriteProjectInfoAsync(projectDir);
        await WriteAssetImagesAsync(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, "config.txt"), """
GenerateCostReportOriginalDocx=true
""");

        var templateService = new FakeTemplateService();
        var builder = CreateBuilder(templateService);

        var result = await builder.BuildAsync(
            projectDir,
            templateDocxPath: Path.Combine(root, "template.docx"),
            configDir,
            outputDir,
            CancellationToken.None);

        File.Exists(result.PngPath).Should().BeTrue();
        result.DocxPath.Should().Be(Path.Combine(outputDir, "成本报表原文件.docx"));
        templateService.CallCount.Should().Be(1);
    }

    private static CostReportBuilder CreateBuilder(ICostReportTemplateService? templateService = null)
    {
        return new CostReportBuilder(
            new TxtProjectInfoParser(),
            new FakeConfigLocator(),
            templateService ?? new OpenXmlCostReportTemplateService(NullLogger<OpenXmlCostReportTemplateService>.Instance));
    }

    private static async Task WriteProjectInfoAsync(string projectDir)
    {
        await File.WriteAllTextAsync(Path.Combine(projectDir, "短剧信息.txt"), """
原剧名: 原剧名
新剧名: 测试短剧
时长: 6 分钟
集数: 2
成本: 3 万元
制作公司: 原公司
""");
    }

    private static async Task WriteAssetImagesAsync(string configDir)
    {
        using var seal = new Image<Rgba32>(128, 128, Color.White);
        seal.ProcessPixelRows(accessor =>
        {
            for (var y = 24; y < 104; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 24; x < 104; x++)
                {
                    row[x] = new Rgba32(220, 0, 0, 255);
                }
            }
        });
        await seal.SaveAsPngAsync(Path.Combine(configDir, "seal.png"));

        using var sign = new Image<Rgba32>(180, 80, Color.White);
        sign.ProcessPixelRows(accessor =>
        {
            for (var y = 20; y < 60; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 30; x < 150; x++)
                {
                    if ((x + y) % 7 == 0)
                    {
                        row[x] = new Rgba32(0, 0, 0, 255);
                    }
                }
            }
        });
        await sign.SaveAsPngAsync(Path.Combine(configDir, "sign.png"));
    }

    private sealed class FakeConfigLocator : IConfigLocator
    {
        public Task<string> FindConfigDirAsync(string projectDir, CancellationToken cancellationToken)
        {
            return Task.FromResult(Path.Combine(Directory.GetParent(projectDir)!.FullName, "config"));
        }
    }

    private sealed class FakeTemplateService : ICostReportTemplateService
    {
        public int CallCount { get; private set; }

        public async Task<string> BuildDocxAsync(CostReportBuildRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            var path = Path.Combine(request.OutputDir, "成本报表原文件.docx");
            await File.WriteAllTextAsync(path, "docx", cancellationToken);
            return path;
        }
    }
}
