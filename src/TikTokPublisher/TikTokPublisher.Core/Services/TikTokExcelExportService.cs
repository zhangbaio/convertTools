using System.Collections.Concurrent;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokExcelExportService
{
    private const string SummarySheet = "汇总";
    private static readonly ConcurrentDictionary<string, object> ExportLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan[] ReplaceRetryDelays =
    [
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromMilliseconds(1200),
    ];

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
        ClientSettings? settings = null,
        IReadOnlyDictionary<string, string>? workspaceByProject = null,
        IReadOnlyList<TikTokAccountProfile>? accountProfiles = null)
    {
        settings ??= ClientSettingsStore.Load();
        var outputPath = ResolveReportPath(account, settings);
        var exportLock = ExportLocks.GetOrAdd(outputPath, static _ => new object());
        lock (exportLock)
        {
            return ExportCore(
                workspace,
                items,
                outputPath,
                workspaceByProject,
                accountProfiles);
        }
    }

    private static string ExportCore(
        string workspace,
        IReadOnlyList<QueueProjectItem> items,
        string outputPath,
        IReadOnlyDictionary<string, string>? workspaceByProject,
        IReadOnlyList<TikTokAccountProfile>? accountProfiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var projectKeys = BuildProjectKeySet(items);
        var manualValues = LoadManualValues(outputPath, projectKeys);
        var accountLookup = BuildAccountLookup(accountProfiles);
        var tempPath = BuildTemporaryReportPath(outputPath);

        try
        {
            using (var document = SpreadsheetDocument.Create(tempPath, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();
                var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
                stylesPart.Stylesheet = BuildStylesheet();
                stylesPart.Stylesheet.Save();

                var sheets = workbookPart.Workbook.AppendChild(new Sheets());
                AppendSheet(
                    workbookPart,
                    sheets,
                    SummarySheet,
                    1U,
                    BuildCurrentQueueRows(
                        workspace,
                        items,
                        manualValues,
                        workspaceByProject,
                        accountLookup));

                workbookPart.Workbook.Save();
            }

            ReplaceReportFileWithRetry(tempPath, outputPath);
            return outputPath;
        }
        finally
        {
            TryDeleteTemporaryReport(tempPath);
        }
    }

    private static string BuildTemporaryReportPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        var fileName = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp.xlsx");
    }

    private static void ReplaceReportFileWithRetry(string tempPath, string outputPath)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= ReplaceRetryDelays.Length; attempt++)
        {
            try
            {
                File.Move(tempPath, outputPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt >= ReplaceRetryDelays.Length)
                    break;

                Thread.Sleep(ReplaceRetryDelays[attempt]);
            }
        }

        throw new IOException(
            $"Excel 报表正在被其他进程占用，自动重试 {ReplaceRetryDelays.Length} 次后仍无法保存：{outputPath}",
            lastError);
    }

    private static void TryDeleteTemporaryReport(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Best-effort cleanup. The completed report is never deleted here.
        }
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildCurrentQueueRows(
        string workspace,
        IReadOnlyList<QueueProjectItem> items,
        IReadOnlyDictionary<string, ManualReviewValue> manualValues,
        IReadOnlyDictionary<string, string>? workspaceByProject,
        IReadOnlyDictionary<string, TikTokAccountProfile> accountLookup)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[]
            {
                "工作目录", "项目目录", "显示名", "原剧名", "新剧名", "集数", "题材（分类）",
                "审核状态", "备注", "简介", "加入时间", "上传完成时间", "当前步骤", "总状态",
                "最后错误", "已归档", "账号", "TIKTOK用户名", "上传"
            }
        };

        foreach (var item in items)
        {
            item.NormalizeStepStates();
            var manual = manualValues.GetValueOrDefault(ProjectKey(item.ProjectDir));
            rows.Add(new object?[]
            {
                ResolveWorkspaceForItem(workspace, item, workspaceByProject),
                item.ProjectDir,
                item.DisplayName,
                item.OriginalTitle,
                item.NewTitle,
                item.EpisodeCount,
                item.GenreCategory,
                manual?.ReviewStatus ?? "",
                FirstNonEmpty(item.Remark, manual?.Notes),
                item.Description,
                item.QueuedAt,
                item.UploadCompletedAt,
                string.IsNullOrWhiteSpace(item.CurrentStep) ? "" : QueueStepRegistry.LabelOf(item.CurrentStep),
                item.StatusText,
                item.LastError,
                item.Archived ? "是" : "否",
                item.AccountProfileName,
                ResolveTikTokUsername(item, accountLookup),
                item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries, ""),
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
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return new Cell
            {
                CellValue = new CellValue(text),
                DataType = CellValues.Number,
                StyleIndex = styleIndex,
            };
        }

        var cellText = value?.ToString() ?? "";
        return new Cell
        {
            CellValue = new CellValue(cellText),
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

    private static HashSet<string> BuildProjectKeySet(IEnumerable<QueueProjectItem> items) =>
        items
            .Select(item => ProjectKey(item.ProjectDir))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, ManualReviewValue> LoadManualValues(
        string outputPath,
        IReadOnlySet<string> targetProjectKeys)
    {
        var values = new Dictionary<string, ManualReviewValue>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(outputPath)) return values;

        try
        {
            using var document = SpreadsheetDocument.Open(outputPath, false);
            var workbookPart = document.WorkbookPart;
            if (workbookPart?.Workbook.Sheets is null) return values;

            var sheets = workbookPart.Workbook.Sheets.OfType<Sheet>().ToList();
            var summarySheet = sheets.FirstOrDefault(sheet =>
                string.Equals(sheet.Name?.Value, SummarySheet, StringComparison.Ordinal));
            if (summarySheet is not null &&
                LoadManualValuesFromSheet(workbookPart, summarySheet, values, targetProjectKeys))
            {
                return values;
            }

            foreach (var sheet in sheets)
            {
                if (ReferenceEquals(sheet, summarySheet)) continue;
                if (LoadManualValuesFromSheet(workbookPart, sheet, values, targetProjectKeys))
                    break;
            }
        }
        catch
        {
            return values;
        }

        return values;
    }

    private static bool LoadManualValuesFromSheet(
        WorkbookPart workbookPart,
        Sheet sheet,
        IDictionary<string, ManualReviewValue> values,
        IReadOnlySet<string> targetProjectKeys)
    {
        if (sheet.Id?.Value is null)
        {
            return false;
        }

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
        var rows = worksheetPart.Worksheet.Descendants<Row>().ToList();
        if (rows.Count <= 1) return false;

        var headers = rows[0].Elements<Cell>().Select(ReadCell).ToList();
        var projectIndex = headers.IndexOf("项目目录");
        var reviewIndex = headers.IndexOf("审核状态");
        var notesIndex = headers.IndexOf("备注");
        if (projectIndex < 0 || (reviewIndex < 0 && notesIndex < 0)) return false;

        foreach (var row in rows.Skip(1))
        {
            var cells = row.Elements<Cell>().Select(ReadCell).ToList();
            var project = CellAt(cells, projectIndex);
            if (string.IsNullOrWhiteSpace(project)) continue;

            var key = ProjectKey(project);
            if (targetProjectKeys.Count > 0 && !targetProjectKeys.Contains(key)) continue;

            var review = CellAt(cells, reviewIndex);
            var notes = CellAt(cells, notesIndex);
            if (string.IsNullOrWhiteSpace(review) && string.IsNullOrWhiteSpace(notes)) continue;

            if (values.TryGetValue(key, out var existing))
            {
                values[key] = new ManualReviewValue(
                    FirstNonEmpty(existing.ReviewStatus, review),
                    FirstNonEmpty(existing.Notes, notes));
            }
            else
            {
                values[key] = new ManualReviewValue(review, notes);
            }

            if (targetProjectKeys.Count > 0 && values.Count >= targetProjectKeys.Count)
                return true;
        }

        return false;
    }

    private static string ReadCell(Cell cell)
    {
        return cell.CellValue?.Text ?? "";
    }

    private static string CellAt(IReadOnlyList<string> cells, int index) =>
        index >= 0 && index < cells.Count ? cells[index].Trim() : "";

    private static string ProjectKey(string? projectDir) =>
        (projectDir ?? "").Trim().Replace('\\', '/').ToLowerInvariant();

    private static string ResolveWorkspaceForItem(
        string fallbackWorkspace,
        QueueProjectItem item,
        IReadOnlyDictionary<string, string>? workspaceByProject)
    {
        if (workspaceByProject is not null &&
            workspaceByProject.TryGetValue(ProjectKey(item.ProjectDir), out var workspace) &&
            !string.IsNullOrWhiteSpace(workspace))
        {
            return workspace;
        }

        return fallbackWorkspace;
    }

    private static IReadOnlyDictionary<string, TikTokAccountProfile> BuildAccountLookup(
        IReadOnlyList<TikTokAccountProfile>? accountProfiles)
    {
        var lookup = new Dictionary<string, TikTokAccountProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accountProfiles ?? [])
        {
            AddAccountLookupKey(lookup, account.Id, account);
            AddAccountLookupKey(lookup, account.Name, account);
            AddAccountLookupKey(lookup, account.DisplayName, account);
            AddAccountLookupKey(lookup, account.TiktokAccountNickname, account);
            AddAccountLookupKey(lookup, account.TiktokLoginEmail, account);
            AddAccountLookupKey(lookup, account.TiktokLastLoginEmail, account);
        }

        return lookup;
    }

    private static void AddAccountLookupKey(
        IDictionary<string, TikTokAccountProfile> lookup,
        string? key,
        TikTokAccountProfile account)
    {
        var text = (key ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(text) && !lookup.ContainsKey(text))
            lookup[text] = account;
    }

    private static string ResolveTikTokUsername(
        QueueProjectItem item,
        IReadOnlyDictionary<string, TikTokAccountProfile> accountLookup)
    {
        foreach (var key in new[] { item.AccountProfileId, item.AccountProfileName })
        {
            if (accountLookup.TryGetValue((key ?? "").Trim(), out var account))
                return FirstNonEmpty(account.ResolveTikTokAccountName(), account.TiktokAccountNickname);
        }

        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return "";
    }

    private sealed record ManualReviewValue(string ReviewStatus, string Notes);
}
