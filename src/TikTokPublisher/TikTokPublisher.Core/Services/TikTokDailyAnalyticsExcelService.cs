using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class TikTokDailyAnalyticsExcelService
{
    public static string Export(string accountName, TikTokDailyAnalyticsReport report, string? outputPath = null)
    {
        if (report.Rows.Count == 0)
            throw new InvalidOperationException("所选日期范围内没有可导出的播放数据。");

        outputPath ??= Path.Combine(
            AppPaths.DataRoot,
            "reports",
            $"{SafeFileName(accountName)}_播放统计_{report.Rows[0].Date:yyyyMMdd}-{report.Rows[^1].Date:yyyyMMdd}.xlsx");
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var document = SpreadsheetDocument.Create(outputPath, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet();
        stylesPart.Stylesheet.Save();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(
            new Columns(
                new Column { Min = 1, Max = 1, Width = 13, CustomWidth = true },
                new Column { Min = 2, Max = 3, Width = 21, CustomWidth = true }),
            sheetData);

        var title = new Row();
        title.Append(TextCell($"TT主体：{accountName}", 2U), TextCell("", 2U), TextCell("", 2U));
        sheetData.Append(title);
        worksheetPart.Worksheet.InsertAfter(new MergeCells(new MergeCell { Reference = "A1:C1" }), sheetData);

        var header = new Row();
        header.Append(TextCell("日期", 1U), TextCell("日有效播放量（万次）", 1U), TextCell("日总播放量（万次）", 1U));
        sheetData.Append(header);

        foreach (var item in report.Rows)
        {
            var row = new Row();
            row.Append(
                TextCell(item.Date.ToString("M.d", CultureInfo.InvariantCulture), 0U),
                NumberCell(item.ValidViews / 10000d, 3U),
                NumberCell(item.TotalViews / 10000d, 3U));
            sheetData.Append(row);
        }

        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1U, Name = "日播放统计" });
        workbookPart.Workbook.Save();
        return outputPath;
    }

    private static Cell TextCell(string value, uint style) => new()
    {
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value)),
        StyleIndex = style,
    };

    private static Cell NumberCell(double value, uint style) => new()
    {
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture)),
        StyleIndex = style,
    };

    private static Stylesheet BuildStylesheet() => new(
        new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164U, FormatCode = "0.00" }) { Count = 1U },
        new Fonts(
            new Font(),
            new Font(new Bold()),
            new Font(new Bold(), new FontSize { Val = 12D })),
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFFFF00" }) { PatternType = PatternValues.Solid })),
        new Borders(new Border()),
        new CellFormats(
            new CellFormat(),
            new CellFormat { FontId = 1U, ApplyFont = true },
            new CellFormat { FontId = 2U, ApplyFont = true },
            new CellFormat { NumberFormatId = 164U, ApplyNumberFormat = true }));

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((value ?? "账号").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "账号" : safe;
    }
}
