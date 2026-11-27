using FluentAssertions;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
            (0, "序号"), (1, "启用"), (2, "原剧名"), (3, "新剧名"), (4, "集数"), (5, "加入时间"),
            (6, "下载"), (7, "视频去重"), (8, "改写"), (9, "海报"), (10, "剧本"),
            (11, "AI素材"), (12, "AI大纲"), (13, "角色图"), (14, "证明材料"),
            (15, "时间戳"), (16, "修复"), (17, "翻译"), (18, "校验"), (19, "上传"),
        };
        foreach (var (column, title) in headers)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Text=\"{title}\"");
        }

        var bindings = new[]
        {
            (6, "DownloadStatus"), (7, "DedupStatus"), (8, "RewriteStatus"),
            (9, "PosterStatus"), (10, "EpisodeScriptStatus"), (11, "AiDramaMaterialsStatus"),
            (12, "AiScriptOutlineStatus"), (13, "RoleVectorStatus"), (14, "ProofMaterialStatus"),
            (15, "TimestampCertificateStatus"), (16, "RepairStatus"), (17, "VideoTranslateStatus"),
            (18, "ValidateStatus"), (19, "UploadStatus"),
        };
        foreach (var (column, binding) in bindings)
        {
            xaml.Should().Contain($"Grid.Column=\"{column}\" Classes=\"stepStatusBadge\" Background=\"{{Binding {binding}BackgroundBrush}}");
        }

        xaml.Should().NotContain("Text=\"账号槽\"");
        xaml.Should().MatchRegex("Grid\\.Column=\"20\"\\s+Classes=\"queueRemarkBox\"");
    }

    [Fact]
    public void Queue_table_columns_and_resize_handles_align_with_runtime_widths()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "TikTokPublisher", "TikTokPublisher.Ui", "Views", "TikTokQueueView.axaml"));
        var code = File.ReadAllText(Path.Combine(
            root, "src", "TikTokPublisher", "TikTokPublisher.Ui", "Views", "TikTokQueueView.axaml.cs"));

        var defaults = ReadArray(code, "QueueTableDefaultColumnWidths");
        var minimums = ReadArray(code, "QueueTableMinColumnWidths");

        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var table = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "QueueTableRootGrid");
        var grids = table.Descendants().Where(element => element.Name.LocalName == "QueueTableGrid").ToArray();
        grids.Should().HaveCount(2);
        minimums.Should().HaveCount(defaults.Length);
        ((int?)table.Attribute("Width")).Should().Be(defaults.Sum(int.Parse));

        foreach (var grid in grids)
        {
            var definitions = ((string)grid.Attribute("ColumnDefinitions")!).Split(',', StringSplitOptions.TrimEntries);
            definitions.Should().Equal(defaults);

            var cells = grid.Elements().Where(element => (string?)element.Attribute("Classes") != "queueColumnResizer");
            cells.Select(element => (int)element.Attribute("Grid.Column")!)
                .Should().Equal(Enumerable.Range(0, defaults.Length));
        }

        var header = grids.Single(grid => (string?)grid.Attribute(x + "Name") == "QueueTableHeaderGrid");
        var handles = header.Elements()
            .Where(element => (string?)element.Attribute("Classes") == "queueColumnResizer")
            .Select(element => ((int)element.Attribute("Grid.Column")!, (int)element.Attribute("Tag")!));
        handles.Should().Equal(Enumerable.Range(0, defaults.Length - 1).Select(column => (column, column)));
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
