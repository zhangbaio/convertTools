using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokExcelExportService
{
    private const string CurrentQueueSheet = "执行项目";
    private const string EventsSheet = "执行流水";

    public static string ResolveReportPath(TikTokAccountProfile? account, ClientSettings? settings = null)
    {
        var raw = (account?.TiktokExcelReportPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = Path.Combine(AppPaths.DataRoot, "reports", "tiktok_upload_records.xlsx");
        var path = Environment.ExpandEnvironmentVariables(raw);
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(path, "tiktok_upload_records.xlsx");
        return Path.GetFullPath(path);
    }

    public static string Export(
        string workspace,
        IReadOnlyList<QueueProjectItem> items,
        TikTokAccountProfile? account,
        ClientSettings? settings = null)
    {
        settings ??= ClientSettingsStore.Load();
        var outputPath = ResolveReportPath(account, settings);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var manualValues = LoadManualValues(outputPath);

        using var document = SpreadsheetDocument.Create(outputPath, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet();
        stylesPart.Stylesheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        AppendSheet(workbookPart, sheets, CurrentQueueSheet, 1, BuildCurrentQueueRows(workspace, items, manualValues));
        AppendSheet(workbookPart, sheets, EventsSheet, 2, BuildEventRows(TikTokExecutionHistoryService.LoadEvents()));
        workbookPart.Workbook.Save();
        return outputPath;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildCurrentQueueRows(
        string workspace,
        IReadOnlyList<QueueProjectItem> items,
        IReadOnlyDictionary<string, ManualReviewValue> manualValues)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[]
            {
                "工作目录", "项目目录", "显示名", "原剧名", "新剧名", "集数", "题材（分类）",
                "审核状态", "备注", "简介", "加入时间", "上传完成时间", "当前步骤", "总状态",
                "最后错误", "已归档", "账号", "账号ID",
                "下载", "改写", "海报", "小文件修复", "静音检测", "静音修复", "素材校验", "删源", "上传"
            }
        };

        foreach (var item in items)
        {
            item.NormalizeStepStates();
            var manual = manualValues.GetValueOrDefault(ProjectKey(item.ProjectDir));
            rows.Add(new object?[]
            {
                workspace,
                item.ProjectDir,
                item.DisplayName,
                item.OriginalTitle,
                item.NewTitle,
                item.EpisodeCount,
                item.GenreCategory,
                manual?.ReviewStatus ?? "",
                manual?.Notes ?? "",
                item.Description,
                item.QueuedAt,
                item.UploadCompletedAt,
                string.IsNullOrWhiteSpace(item.CurrentStep) ? "" : QueueStepRegistry.LabelOf(item.CurrentStep),
                item.StatusText,
                item.LastError,
                item.Archived ? "是" : "否",
                item.AccountProfileName,
                item.AccountProfileId,
                item.StepStates.GetValueOrDefault(QueueStepKeys.Download, ""),
                item.StepStates.GetValueOrDefault(QueueStepKeys.RewriteInfo, ""),
                item.StepStates.GetValueOrDefault(QueueStepKeys.GeneratePoster, ""),
                item.StepStates.GetValueOrDefault(QueueStepKeys.SmallVideoRepair, ""),
                item.StepStates.GetValueOrDefault(QueueStepKeys.SilenceDetect, ""),
                item.StepStates.GetValueOrDefault(QueueStepKeys.SilenceRepair, ""),
                item.StepStates.GetValueOrDefault(QueueStepKeys.MaterialValidate, ""),
                item.StepStates.GetValueOrDefault(QueueStepKeys.DeleteSourceVideos, ""),
                item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries, ""),
            });
        }

        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildEventRows(IReadOnlyList<Dictionary<string, object?>> events)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[]
            {
                "时间", "批次ID", "事件类型", "状态", "工作目录", "项目目录", "显示名",
                "原剧名", "新剧名", "步骤", "消息", "错误", "账号", "机器"
            }
        };

        foreach (var e in events)
        {
            rows.Add(new object?[]
            {
                Text(e, "timestamp"),
                Text(e, "batch_id"),
                Text(e, "event_type"),
                Text(e, "status"),
                Text(e, "workspace"),
                Text(e, "project_dir"),
                Text(e, "display_name"),
                Text(e, "original_title"),
                Text(e, "new_title"),
                Text(e, "step_label"),
                Text(e, "message"),
                Text(e, "error"),
                Text(e, "account_profile_name"),
                Text(e, "machine_name"),
            });
        }

        return rows;
    }

    private static void AppendSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        string name,
        uint sheetId,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var worksheet = new Worksheet(sheetData);
        worksheetPart.Worksheet = worksheet;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new Row { RowIndex = (uint)rowIndex + 1 };
            var values = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < values.Count; columnIndex++)
            {
                row.Append(CreateCell(values[columnIndex], rowIndex == 0 ? 1U : 0U));
            }
            sheetData.Append(row);
        }

        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = name,
        });
    }

    private static Cell CreateCell(object? value, uint styleIndex)
    {
        if (value is int or long or double or float or decimal)
        {
            return new Cell
            {
                CellValue = new CellValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType = CellValues.Number,
                StyleIndex = styleIndex,
            };
        }

        return new Cell
        {
            CellValue = new CellValue(value?.ToString() ?? ""),
            DataType = CellValues.String,
            StyleIndex = styleIndex,
        };
    }

    private static Stylesheet BuildStylesheet()
    {
        return new Stylesheet(
            new Fonts(
                new Font(),
                new Font(new Bold(), new Color { Rgb = "FFFFFFFF" })),
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FF1F4E78" }) { PatternType = PatternValues.Solid })),
            new Borders(new Border()),
            new CellFormats(
                new CellFormat(),
                new CellFormat { FontId = 1, FillId = 2, BorderId = 0, ApplyFill = true, ApplyFont = true }));
    }

    private static IReadOnlyDictionary<string, ManualReviewValue> LoadManualValues(string outputPath)
    {
        var values = new Dictionary<string, ManualReviewValue>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(outputPath)) return values;

        try
        {
            using var document = SpreadsheetDocument.Open(outputPath, false);
            var workbookPart = document.WorkbookPart;
            if (workbookPart?.Workbook.Sheets is null) return values;
            var sheet = workbookPart.Workbook.Sheets.OfType<Sheet>()
                .FirstOrDefault(s => string.Equals(s.Name, CurrentQueueSheet, StringComparison.Ordinal));
            if (sheet?.Id?.Value is null) return values;
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            var rows = worksheetPart.Worksheet.Descendants<Row>().ToList();
            if (rows.Count <= 1) return values;
            var headers = rows[0].Elements<Cell>().Select(ReadCell).ToList();
            var projectIndex = headers.IndexOf("项目目录");
            var reviewIndex = headers.IndexOf("审核状态");
            var notesIndex = headers.IndexOf("备注");
            if (projectIndex < 0 || (reviewIndex < 0 && notesIndex < 0)) return values;
            foreach (var row in rows.Skip(1))
            {
                var cells = row.Elements<Cell>().Select(ReadCell).ToList();
                var project = CellAt(cells, projectIndex);
                if (string.IsNullOrWhiteSpace(project)) continue;
                var review = CellAt(cells, reviewIndex);
                var notes = CellAt(cells, notesIndex);
                if (!string.IsNullOrWhiteSpace(review) || !string.IsNullOrWhiteSpace(notes))
                    values[ProjectKey(project)] = new ManualReviewValue(review, notes);
            }
        }
        catch
        {
            return values;
        }

        return values;
    }

    private static string ReadCell(Cell cell)
    {
        return cell.CellValue?.Text ?? "";
    }

    private static string CellAt(IReadOnlyList<string> cells, int index) =>
        index >= 0 && index < cells.Count ? cells[index].Trim() : "";

    private static string ProjectKey(string? projectDir) =>
        (projectDir ?? "").Trim().Replace('\\', '/').ToLowerInvariant();

    private static string Text(IReadOnlyDictionary<string, object?> payload, string key) =>
        payload.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private sealed record ManualReviewValue(string ReviewStatus, string Notes);
}
