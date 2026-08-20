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
            (12, "AI大纲"), (13, "角色图"), (14, "证明材料"), (15, "时间戳"),
            (16, "修复"), (17, "翻译"), (18, "静音检测"), (19, "静音修复"),
            (20, "校验"), (21, "删源"), (22, "上传"),
        };
        foreach (var (column, title) in headers)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Text=\"{title}\"");
        }

        var bindings = new[]
        {
            (7, "DownloadStatus"), (8, "RewriteStatus"), (9, "PosterStatus"),
            (10, "EpisodeScriptStatus"), (11, "AiDramaMaterialsStatus"), (12, "AiScriptOutlineStatus"),
            (13, "RoleVectorStatus"), (14, "ProofMaterialStatus"), (15, "TimestampCertificateStatus"),
            (16, "RepairStatus"), (17, "VideoTranslateStatus"), (18, "SilenceDetectStatus"),
            (19, "SilenceRepairStatus"), (20, "ValidateStatus"), (21, "DeleteSourceStatus"),
            (22, "UploadStatus"),
        };
        foreach (var (column, binding) in bindings)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Classes=\"stepStatusBadge\" Background=\"{{Binding {binding}BackgroundBrush}}");
        }

        xaml.Should().Contain("Grid.Column=\"23\"\n                                                 Classes=\"queueRemarkBox\"");
        xaml.Should().Contain("ColumnDefinitions=\"48,56,104,210,210,60,128,68,68,68,68,68,68,68,76,68,68,0,68,68,68,68,68,180\"");
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
