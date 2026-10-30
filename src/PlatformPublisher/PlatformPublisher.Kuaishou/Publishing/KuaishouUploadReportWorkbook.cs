using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PlatformPublisher.Kuaishou.Publishing;

internal static class KuaishouUploadReportWorkbook
{
    private static readonly string[] TimelineHeaders =
    [
        "记录时间", "日期", "上传状态", "执行模式", "执行步骤", "项目名称", "原剧名", "新剧名", "作者",
        "项目路径", "剧集数", "已上传集数", "第一页提交动作", "当前阶段", "下载", "改写信息", "生成海报",
        "生成工程图", "补齐字段", "上传剧集", "账号档案", "本机设备名", "失败原因", "prepare_state_path", "upload_state_path",
    ];

    private static readonly string[] DashboardHeaders =
    [
        "记录时间", "账号档案", "项目名称", "原剧名", "新剧名", "作者", "上传状态", "第一页提交动作",
        "当前阶段", "剧集数", "上传进度", "下载", "改写信息", "生成海报", "生成工程图", "补齐字段",
        "失败原因", "项目路径",
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
            var dashboardName = enterprise ? "快手分账企业版看板" : "快手分账个人版看板";
            AddSheet(workbookPart, sheets, dashboardName, DashboardHeaders, DashboardRows(records), 1u);
            AddSheet(workbookPart, sheets, "执行流水", TimelineHeaders, records.Select(record =>
                TimelineHeaders.Select(header => record.GetValueOrDefault(header) ?? "").ToArray()).ToArray(), 2u);
            workbookPart.Workbook.Save();
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static IReadOnlyList<string[]> DashboardRows(IReadOnlyList<Dictionary<string, string>> records)
    {
        var merged = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var record in records.OrderBy(item => item.GetValueOrDefault("记录时间") ?? "", StringComparer.Ordinal))
        {
            var key = First(
                record.GetValueOrDefault("项目路径"),
                string.Join("|", record.GetValueOrDefault("原剧名") ?? "", record.GetValueOrDefault("新剧名") ?? ""),
                record.GetValueOrDefault("项目名称"));
            if (string.IsNullOrWhiteSpace(key.Replace("|", "", StringComparison.Ordinal)))
                key = Guid.NewGuid().ToString("N");
            if (!merged.TryGetValue(key, out var current))
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                merged[key] = current;
                order.Add(key);
            }
            foreach (var pair in record)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    current[pair.Key] = pair.Value;
            }
        }

        return order.Select(key => DashboardRow(merged[key])).ToArray();
    }

    private static string[] DashboardRow(Dictionary<string, string> record)
    {
        var uploaded = record.GetValueOrDefault("已上传集数") ?? "";
        var total = record.GetValueOrDefault("剧集数") ?? "";
        var progress = !string.IsNullOrWhiteSpace(uploaded) && !string.IsNullOrWhiteSpace(total)
            ? $"{uploaded}/{total}"
            : First(uploaded, total);
        return
        [
            record.GetValueOrDefault("记录时间") ?? "",
            record.GetValueOrDefault("账号档案") ?? "",
            record.GetValueOrDefault("项目名称") ?? "",
            record.GetValueOrDefault("原剧名") ?? "",
            record.GetValueOrDefault("新剧名") ?? "",
            record.GetValueOrDefault("作者") ?? "",
            record.GetValueOrDefault("上传状态") ?? "",
            record.GetValueOrDefault("第一页提交动作") ?? "",
            record.GetValueOrDefault("当前阶段") ?? "",
            total,
            progress,
            record.GetValueOrDefault("下载") ?? "",
            record.GetValueOrDefault("改写信息") ?? "",
            record.GetValueOrDefault("生成海报") ?? "",
            record.GetValueOrDefault("生成工程图") ?? "",
            record.GetValueOrDefault("补齐字段") ?? "",
            record.GetValueOrDefault("失败原因") ?? "",
            record.GetValueOrDefault("项目路径") ?? "",
        ];
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

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
