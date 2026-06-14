using FluentAssertions;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Workflow;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Workflow;

public sealed class ProjectScannerTests
{
    [Fact]
    public async Task ScanAsync_Should_Merge_Source_Workflow_And_Backup_State()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var workflowRoot = Directory.CreateDirectory(Path.Combine(root, "workflow")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "config"));

        var backupRoot = Directory.CreateTempSubdirectory().FullName;
        var sourceDir = Directory.CreateDirectory(Path.Combine(root, "婆婆")).FullName;
        var workflowDir = Directory.CreateDirectory(Path.Combine(workflowRoot, "_假面婆婆不好惹")).FullName;
        var backupDir = Directory.CreateDirectory(Path.Combine(backupRoot, "婆婆")).FullName;

        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "第01集.mp4"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(workflowDir, "短剧信息.txt"), """
原剧名: 婆婆
新剧名: 假面婆婆不好惹
时长: 6 分钟
集数: 1
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");
        await File.WriteAllTextAsync(Path.Combine(workflowDir, "states.json"), """
[
  { "type": "Transcode", "isCompleted": true, "error": null },
  { "type": "RewriteProjectInfo", "isCompleted": true, "error": null }
]
""");
        await File.WriteAllTextAsync(Path.Combine(backupDir, "states.json"), """
[
  { "type": "Transcode", "isCompleted": true, "error": null },
  { "type": "RewriteProjectInfo", "isCompleted": true, "error": null },
  { "type": "GeneratePosterImage", "isCompleted": false, "error": "failed" }
]
""");

        var scanner = new ProjectScanner(new TxtProjectInfoParser());

        var result = await scanner.ScanAsync(root, backupRoot, CancellationToken.None);

        result.TotalProjects.Should().Be(1);
        result.PendingProjects.Should().Be(1);
        result.Projects.Should().ContainSingle();

        var project = result.Projects[0];
        project.SourceName.Should().Be("婆婆");
        project.DisplayName.Should().Be("假面婆婆不好惹");
        project.Status.Should().Be("处理中");
        project.VideoCount.Should().Be(1);
        project.CompletedSteps.Should().Be(2);
        project.ResumeFrom.Should().Be("workflow");
        project.WorkflowProjectDir.Should().Be(workflowDir);
        project.BackupProjectDir.Should().Be(backupDir);
    }

    [Fact]
    public async Task ScanAsync_Should_Report_Completed_Project()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var workflowRoot = Directory.CreateDirectory(Path.Combine(root, "workflow")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "config"));

        var sourceDir = Directory.CreateDirectory(Path.Combine(root, "婆婆")).FullName;
        var workflowDir = Directory.CreateDirectory(Path.Combine(workflowRoot, "假面婆婆不好惹")).FullName;
        var videosDir = Directory.CreateDirectory(Path.Combine(workflowDir, "videos")).FullName;

        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "第01集.mp4"), [1]);
        await File.WriteAllTextAsync(Path.Combine(workflowDir, "短剧信息.txt"), """
原剧名: 婆婆
新剧名: 假面婆婆不好惹
时长: 6 分钟
集数: 1
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");
        await File.WriteAllBytesAsync(Path.Combine(workflowDir, "海报图片.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(workflowDir, "成本报表.png"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(workflowDir, "工程图_01.png"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(videosDir, "假面婆婆不好惹-第1集.mp4"), [1]);
        await File.WriteAllTextAsync(Path.Combine(workflowDir, "states.json"), """
[
  { "type": "Transcode", "isCompleted": true, "error": null },
  { "type": "RewriteProjectInfo", "isCompleted": true, "error": null },
  { "type": "GeneratePosterImage", "isCompleted": true, "error": null },
  { "type": "GenerateProjectImages", "isCompleted": true, "error": null },
  { "type": "GenerateCostReportImage", "isCompleted": true, "error": null },
  { "type": "RenameFiles", "isCompleted": true, "error": null }
]
""");

        var scanner = new ProjectScanner(new TxtProjectInfoParser());

        var result = await scanner.ScanAsync(root, backupRootDir: null, CancellationToken.None);

        result.PendingProjects.Should().Be(0);
        result.Projects.Should().ContainSingle();
        result.Projects[0].Status.Should().Be("已完成");
        result.Projects[0].ResumeFrom.Should().Be("workflow");
    }

    [Fact]
    public async Task ScanAsync_Should_Prefer_Source_Metadata_Workflow_Binding()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(root, "辣翻小河村")).FullName;
        var expectedWorkflowDir = Path.Combine(root, "workflow", "别名工作流目录");
        Directory.CreateDirectory(expectedWorkflowDir);

        await File.WriteAllTextAsync(Path.Combine(sourceDir, "shortdrama-project.json"), $$"""
{
  "projectKey": "辣翻小河村",
  "sourceName": "辣翻小河村",
  "displayName": "辣翻小河村",
  "bookId": "bk-1",
  "workflowDirName": "别名工作流目录",
  "workflowProjectDir": "{{expectedWorkflowDir.Replace("\\", "\\\\")}}"
}
""");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "shortdrama-state.json"), """
{
  "projectKey": "辣翻小河村",
  "displayName": "辣翻小河村",
  "completed": false,
  "workflowProjectDir": "/tmp/placeholder",
  "steps": [
    { "type": "Transcode", "isCompleted": true, "error": null }
  ]
}
""");

        var scanner = new ProjectScanner(new TxtProjectInfoParser());

        var result = await scanner.ScanAsync(root, backupRootDir: null, CancellationToken.None);

        result.Projects.Should().ContainSingle();
        result.Projects[0].WorkflowProjectDir.Should().Be(expectedWorkflowDir);
        result.Projects[0].ResumeFrom.Should().Be("source");
        result.Projects[0].CompletedSteps.Should().Be(1);
    }

    [Fact]
    public async Task ScanAsync_Should_Fallback_When_Metadata_Workflow_Path_Is_Stale()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var workflowRoot = Directory.CreateDirectory(Path.Combine(root, "workflow")).FullName;
        var sourceDir = Directory.CreateDirectory(Path.Combine(root, "亲戚别来我家")).FullName;
        var actualWorkflowDir = Directory.CreateDirectory(Path.Combine(workflowRoot, "忍够三年亲戚占房我拎包走人")).FullName;
        var staleWorkflowDir = Path.Combine(workflowRoot, "_忍够三年亲戚占房我拎包走人");

        await File.WriteAllTextAsync(Path.Combine(sourceDir, "shortdrama-project.json"), $$"""
{
  "projectKey": "亲戚别来我家",
  "sourceName": "亲戚别来我家",
  "displayName": "忍够三年亲戚占房我拎包走人",
  "workflowDirName": "_忍够三年亲戚占房我拎包走人",
  "workflowProjectDir": "{{staleWorkflowDir.Replace("\\", "\\\\")}}"
}
""");

        await File.WriteAllTextAsync(Path.Combine(sourceDir, "shortdrama-state.json"), $$"""
{
  "projectKey": "亲戚别来我家",
  "displayName": "忍够三年亲戚占房我拎包走人",
  "workflowProjectDir": "{{staleWorkflowDir.Replace("\\", "\\\\")}}",
  "completed": false,
  "steps": [
    { "type": "rewrite", "isCompleted": true, "error": null }
  ]
}
""");

        await File.WriteAllTextAsync(Path.Combine(actualWorkflowDir, "短剧信息.txt"), """
原剧名: 亲戚别来我家
新剧名: 忍够三年亲戚占房我拎包走人
时长: 43 分钟
集数: 41
成本: 6 万元
制作公司: 武汉云起漫影科技有限公司
""");

        var scanner = new ProjectScanner(new TxtProjectInfoParser());

        var result = await scanner.ScanAsync(root, backupRootDir: null, CancellationToken.None);

        result.Projects.Should().ContainSingle();
        result.Projects[0].WorkflowProjectDir.Should().Be(actualWorkflowDir);
    }
}
