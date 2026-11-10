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
            (7, "下载"), (8, "视频去重"), (9, "改写"), (10, "海报"), (11, "剧本"),
            (12, "AI素材"), (13, "AI大纲"), (14, "角色图"), (15, "证明材料"),
            (16, "时间戳"), (17, "修复"), (18, "翻译"), (19, "校验"), (20, "上传"),
        };
        foreach (var (column, title) in headers)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Text=\"{title}\"");
        }

        var bindings = new[]
        {
            (7, "DownloadStatus"), (8, "DedupStatus"), (9, "RewriteStatus"),
            (10, "PosterStatus"), (11, "EpisodeScriptStatus"), (12, "AiDramaMaterialsStatus"),
            (13, "AiScriptOutlineStatus"), (14, "RoleVectorStatus"), (15, "ProofMaterialStatus"),
            (16, "TimestampCertificateStatus"), (17, "RepairStatus"), (18, "VideoTranslateStatus"),
            (19, "ValidateStatus"), (20, "UploadStatus"),
        };
        foreach (var (column, binding) in bindings)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Classes=\"stepStatusBadge\" Background=\"{{Binding {binding}BackgroundBrush}}");
        }

        xaml.Should().MatchRegex("Grid\\.Column=\"21\"\\s+Classes=\"queueRemarkBox\"");
        xaml.Should().Contain("ColumnDefinitions=\"48,56,104,210,210,60,128,68,68,68,68,68,68,68,68,76,68,68,0,68,68,180\"");
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
