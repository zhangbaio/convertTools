using FluentAssertions;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokQueueColumnOrderTests
{
    [Fact]
    public void Queue_table_step_columns_follow_enabled_step_order()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "TikTokPublisher", "TikTokPublisher.Ui", "Views", "TikTokQueueView.axaml"));

        var headers = new[]
        {
            (7, "下载"), (8, "改写"), (9, "海报"), (10, "剧本"), (11, "AI素材"),
            (12, "AI大纲"), (13, "证明材料"), (14, "时间戳"), (15, "修复"),
            (16, "翻译"), (17, "静音检测"), (18, "静音修复"), (19, "校验"),
            (20, "删源"), (21, "上传"),
        };
        foreach (var (column, title) in headers)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Text=\"{title}\"");
        }

        var bindings = new[]
        {
            (7, "DownloadStatus"), (8, "RewriteStatus"), (9, "PosterStatus"),
            (10, "EpisodeScriptStatus"), (11, "AiDramaMaterialsStatus"), (12, "AiScriptOutlineStatus"),
            (13, "ProofMaterialStatus"), (14, "TimestampCertificateStatus"), (15, "RepairStatus"),
            (16, "VideoTranslateStatus"), (17, "SilenceDetectStatus"), (18, "SilenceRepairStatus"),
            (19, "ValidateStatus"), (20, "DeleteSourceStatus"), (21, "UploadStatus"),
        };
        foreach (var (column, binding) in bindings)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Classes=\"stepStatusBadge\" Background=\"{{Binding {binding}BackgroundBrush}}");
        }

        xaml.Should().Contain("Grid.Column=\"22\"\n                                                 Classes=\"queueRemarkBox\"");
        xaml.Should().Contain("ColumnDefinitions=\"48,56,104,210,210,60,128,68,68,68,68,68,68,76,68,68,0,68,68,68,68,68,180\"");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ConvertTools.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 ConvertTools.sln。");
    }
}
