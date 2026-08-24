using FluentAssertions;
using System.Text.RegularExpressions;
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
            (16, "修复"), (17, "翻译"), (18, "校验"), (19, "删源"), (20, "上传"),
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
            (16, "RepairStatus"), (17, "VideoTranslateStatus"), (18, "ValidateStatus"),
            (19, "DeleteSourceStatus"), (20, "UploadStatus"),
        };
        foreach (var (column, binding) in bindings)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Classes=\"stepStatusBadge\" Background=\"{{Binding {binding}BackgroundBrush}}");
        }

        xaml.Should().Contain("Grid.Column=\"21\"\n                                                 Classes=\"queueRemarkBox\"");
        xaml.Should().Contain("ColumnDefinitions=\"48,56,104,210,210,60,128,68,68,68,68,68,68,68,76,68,68,0,68,68,68,180\"");
    }

    [Fact]
    public void Queue_table_runtime_width_arrays_match_xaml_column_count()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "TikTokPublisher", "TikTokPublisher.Ui", "Views", "TikTokQueueView.axaml"));
        var code = File.ReadAllText(Path.Combine(
            root, "src", "TikTokPublisher", "TikTokPublisher.Ui", "Views", "TikTokQueueView.axaml.cs"));

        var definitions = Regex.Match(
            xaml,
            "QueueTableHeaderGrid[^>]+ColumnDefinitions=\"(?<values>[^\"]+)\"")
            .Groups["values"].Value.Split(',', StringSplitOptions.TrimEntries);
        var defaults = ReadArray(code, "QueueTableDefaultColumnWidths");
        var minimums = ReadArray(code, "QueueTableMinColumnWidths");

        defaults.Should().HaveCount(definitions.Length);
        minimums.Should().HaveCount(definitions.Length);
    }

    private static string[] ReadArray(string code, string fieldName) =>
        Regex.Match(
                code,
                $@"{fieldName}\s*=\s*\{{(?<values>.*?)\}};",
                RegexOptions.Singleline)
            .Groups["values"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
