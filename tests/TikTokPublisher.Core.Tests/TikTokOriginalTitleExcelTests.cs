using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public class TikTokOriginalTitleExcelTests
{
    [Fact]
    public void Load_Uses_The_Latest_Upload_Time_For_The_Same_New_Title()
    {
        var path = Path.Combine(Path.GetTempPath(), $"original-title-{Guid.NewGuid():N}.xlsx");
        try
        {
            WriteWorkbook(path,
            [
                ["新剧名", "原剧名", "上传完成时间"],
                ["大壮新名", "旧版本", "2026-07-01 08:00:00"],
                ["大壮新名", "大壮之重生卤菜大王", "2026-07-05 04:28:00"],
                ["另一新名", "安猪下水", "2026-07-06 01:00:00"],
            ]);

            var index = TikTokOriginalTitleIndex.Load(path);

            index.Find("大壮新名")!.OriginalTitle.Should().Be("大壮之重生卤菜大王");
            index.Find("另一新名")!.OriginalTitle.Should().Be("安猪下水");
            index.Find("不存在的新剧名").Should().BeNull();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void WriteWorkbook(string path, IReadOnlyList<string[]> rows)
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
