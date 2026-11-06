using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PlatformPublisher.Kuaishou.Publishing;

internal static class KuaishouUploadReportWorkbook
{
    private static readonly string[] Headers =
    [
        "记录时间", "日期", "账号档案", "快手昵称", "项目名称", "原剧名", "新剧名", "作者",
        "集数", "上传状态", "失败原因", "项目路径",
    ];

    public static void Write(IReadOnlyList<Dictionary<string, string>> records, string path, bool enterprise)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        if (File.Exists(temporary))
            File.Delete(temporary);
        using (var document = SpreadsheetDocument.Create(temporary, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = CreateStylesheet();
            styles.Stylesheet.Save();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            var sheetName = enterprise ? "快手分账企业版" : "快手分账个人版";
            var rows = records.Select(record => Headers.Select(header => record.GetValueOrDefault(header) ?? "").ToArray()).ToArray();
            AddSheet(workbookPart, sheets, sheetName, Headers, rows, 1u);
            workbookPart.Workbook.Save();
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void AddSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        string name,
        IReadOnlyList<string> headers,
        IReadOnlyList<string[]> rows,
        uint sheetId)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        sheetData.Append(CreateRow(1, headers, header: true));
        for (var index = 0; index < rows.Count; index++)
            sheetData.Append(CreateRow(index + 2, rows[index], header: false));
        var columns = new Columns();
        for (var index = 0; index < headers.Count; index++)
        {
            columns.Append(new Column
            {
                Min = (uint)(index + 1),
                Max = (uint)(index + 1),
                Width = 18,
                CustomWidth = true,
            });
        }
        worksheetPart.Worksheet = new Worksheet(
            new SheetViews(new SheetView(new Pane
            {
                VerticalSplit = 1,
                TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            })
            { WorkbookViewId = 0 }),
            columns,
            sheetData);
        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = name,
        });
    }

    private static Row CreateRow(int rowIndex, IReadOnlyList<string> values, bool header)
    {
        var row = new Row { RowIndex = (uint)rowIndex };
        for (var index = 0; index < values.Count; index++)
        {
            row.Append(new Cell
            {
                CellReference = ColumnName(index) + rowIndex,
                DataType = CellValues.InlineString,
                StyleIndex = header ? 1u : 0u,
                InlineString = new InlineString(new Text(values[index] ?? "") { Space = SpaceProcessingModeValues.Preserve }),
            });
        }
        return row;
    }

    private static Stylesheet CreateStylesheet() => new(
        new Fonts(
            new Font(new FontSize { Val = 11 }, new FontName { Val = "Microsoft YaHei" }),
            new Font(new Bold(), new FontSize { Val = 12 }, new FontName { Val = "Microsoft YaHei" })),
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FF43C0BA" }) { PatternType = PatternValues.Solid })),
        new Borders(
            new Border(),
            new Border(
                new LeftBorder { Style = BorderStyleValues.Thin },
                new RightBorder { Style = BorderStyleValues.Thin },
                new TopBorder { Style = BorderStyleValues.Thin },
                new BottomBorder { Style = BorderStyleValues.Thin })),
        new CellFormats(
            new CellFormat { FontId = 0, FillId = 0, BorderId = 1, ApplyBorder = true },
            new CellFormat { FontId = 1, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true }));

    private static string ColumnName(int index)
    {
        var name = "";
        var value = index;
        do
        {
            name = (char)('A' + value % 26) + name;
            value = value / 26 - 1;
        }
        while (value >= 0);
        return name;
    }
}
