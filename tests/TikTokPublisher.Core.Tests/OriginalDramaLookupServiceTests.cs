using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public class OriginalDramaLookupServiceTests
{
    [Fact]
    public async Task Lookup_Runs_Searches_Together_And_Keeps_Input_Order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"original-lookup-{Guid.NewGuid():N}.xlsx");
        try
        {
            WriteTitleWorkbook(path,
            [
                ["新剧名", "原剧名", "上传完成时间"],
                ["新甲", "原甲", "2026-07-01 08:00:00"],
                ["新乙", "原乙", "2026-07-02 08:00:00"],
            ]);
            var index = TikTokOriginalTitleIndex.Load(path);
            var entered = 0;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var rows = await OriginalDramaLookupService.LookupCoreAsync(
                ["新乙", "新甲", "没有的剧"],
                index,
                OriginalDramaLookupService.SearchConcurrency,
                async (title, token) =>
                {
                    if (Interlocked.Increment(ref entered) == 2)
                        release.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                    if (title == "原甲")
                        await Task.Delay(30, token);
                    return new HongguoDramaLookupHit(
                        title,
                        "id-" + title,
                        title == "原甲" ? "竖屏" : "横屏",
                        "种田",
                        [new HongguoDramaField("作者", "夜珠")],
                        []);
                },
                progress: null,
                CancellationToken.None);

            entered.Should().Be(2);
            rows.Select(row => row.NewTitle).Should().Equal("新乙", "新甲", "没有的剧");
            rows[0].OriginalTitle.Should().Be("原乙");
            rows[0].Orientation.Should().Be("横屏");
            rows[0].Genre.Should().Be("种田");
            rows[1].Status.Should().Be("已找到");
            rows[1].Orientation.Should().Be("竖屏");
            rows[2].Status.Should().Be("未找到原剧名");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Export_Writes_Orientation_Genre_And_One_Row_Per_Title()
    {
        var rows = new[]
        {
            new OriginalDramaLookupRow(
                "新甲",
                "原甲",
                "已找到",
                "竖屏",
                "种田",
                "hghigh:1",
                [new HongguoDramaField("作者", "夜珠文化传媒"), new HongguoDramaField("横竖屏", "竖屏")],
                []),
            new OriginalDramaLookupRow("新乙", "", "未找到原剧名", "", "", "", [], []),
        };

        using var stream = new MemoryStream();
        OriginalDramaLookupService.Export(stream, rows);
        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet;
        var values = sheet.Descendants<Row>()
            .Select(row => row.Elements<Cell>().Select(cell => cell.InlineString?.InnerText ?? "").ToArray())
            .ToArray();

        values[0].Should().ContainInOrder("新剧名", "原剧名", "状态", "横竖屏", "题材", "内容类型", "作者");
        values[0].Should().NotContain("作品ID");
        values[1][0].Should().Be("新甲");
        values[1][3].Should().Be("竖屏");
        values[1][4].Should().Be("种田");
        values[1][6].Should().Be("夜珠文化传媒");
        values[2][0].Should().Be("新乙");
        values[2][2].Should().Be("未找到原剧名");
    }

    private static void WriteTitleWorkbook(string path, IReadOnlyList<string[]> rows)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(sheetData);
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "汇总",
        });
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new Row { RowIndex = (uint)rowIndex + 1 };
            for (var column = 0; column < rows[rowIndex].Length; column++)
            {
                row.Append(new Cell
                {
                    CellReference = ((char)('A' + column)).ToString() + (rowIndex + 1),
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(rows[rowIndex][column])),
                });
            }

            sheetData.Append(row);
        }

        workbookPart.Workbook.Save();
    }
}
