using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TikTokPublisher.Core.Services;

public sealed record OriginalTitleRecord(string NewTitle, string OriginalTitle, string CompletedAt);

public sealed class TikTokOriginalTitleIndex
{
    private readonly Dictionary<string, OriginalTitleRecord> _byNewTitle;

    private TikTokOriginalTitleIndex(Dictionary<string, OriginalTitleRecord> byNewTitle)
    {
        _byNewTitle = byNewTitle;
    }

    public int Count => _byNewTitle.Count;

    public OriginalTitleRecord? Find(string newTitle)
    {
        var key = Normalize(newTitle);
        return key.Length == 0 ? null : _byNewTitle.GetValueOrDefault(key);
    }

    public static TikTokOriginalTitleIndex Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("未找到上传记录 Excel。");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = SpreadsheetDocument.Open(stream, false);
            var workbookPart = document.WorkbookPart;
            var sheet = workbookPart?.Workbook.Sheets?
                    .Elements<Sheet>()
                    .FirstOrDefault(item => string.Equals(item.Name?.Value, "汇总", StringComparison.Ordinal))
                ?? workbookPart?.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault();
            if (workbookPart is null || sheet?.Id?.Value is null)
                throw new InvalidOperationException("上传记录 Excel 没有可用的工作表。");

            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            var rows = worksheetPart.Worksheet.Descendants<Row>().ToArray();
            if (rows.Length == 0)
                throw new InvalidOperationException("上传记录 Excel 是空的。");

            var sharedStrings = workbookPart.SharedStringTablePart?
                .SharedStringTable
                .Elements<SharedStringItem>()
                .Select(item => item.InnerText)
                .ToArray() ?? [];
            var headers = ReadRow(rows[0], sharedStrings)
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .GroupBy(pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);
            var newColumn = FindHeader(headers, "新剧名", "新剧名称");
            var originalColumn = FindHeader(headers, "原剧名", "原剧名称", "原始剧名");
            if (newColumn < 0 || originalColumn < 0)
                throw new InvalidOperationException("上传记录缺少「新剧名」或「原剧名」列。");
            var completedColumn = FindHeader(headers, "上传完成时间");
            var queuedColumn = FindHeader(headers, "加入时间");

            var matches = new Dictionary<string, (OriginalTitleRecord Record, DateTime CompletedAt, int Order)>(StringComparer.Ordinal);
            var order = 0;
            foreach (var row in rows.Skip(1))
            {
                order++;
                var values = ReadRow(row, sharedStrings);
                var newTitle = values.GetValueOrDefault(newColumn, "").Trim();
                var originalTitle = values.GetValueOrDefault(originalColumn, "").Trim();
                if (newTitle.Length == 0 || originalTitle.Length == 0)
                    continue;
                var completedText = completedColumn >= 0 ? values.GetValueOrDefault(completedColumn, "").Trim() : "";
                if (completedText.Length == 0 && queuedColumn >= 0)
                    completedText = values.GetValueOrDefault(queuedColumn, "").Trim();
                var key = Normalize(newTitle);
                if (key.Length == 0)
                    continue;
                if (!DateTime.TryParse(completedText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var completedAt) &&
                    !DateTime.TryParse(completedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out completedAt))
                {
                    completedAt = DateTime.MinValue;
                }
                var record = new OriginalTitleRecord(newTitle, originalTitle, completedText);
                if (!matches.TryGetValue(key, out var current) ||
                    completedAt > current.CompletedAt ||
                    completedAt == current.CompletedAt && order > current.Order)
                {
                    matches[key] = (record, completedAt, order);
                }
            }

            return new TikTokOriginalTitleIndex(
                matches.ToDictionary(pair => pair.Key, pair => pair.Value.Record, StringComparer.Ordinal));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new InvalidOperationException("无法读取上传记录，请先关闭正在打开的 Excel。");
        }
        catch (OpenXmlPackageException)
        {
            throw new InvalidOperationException("上传记录 Excel 无法打开。");
        }
    }

    public static string Normalize(string? value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).ToArray());

    private static Dictionary<int, string> ReadRow(Row row, IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<int, string>();
        var fallbackIndex = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            var index = ColumnIndex(cell.CellReference?.Value);
            if (index < 0)
                index = fallbackIndex;
            values[index] = ReadCell(cell, sharedStrings);
            fallbackIndex = index + 1;
        }

        return values;
    }

    private static string ReadCell(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";
        var raw = cell.CellValue?.Text ?? cell.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 &&
            index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }

        return raw;
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return -1;
        var value = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch))
                break;
            value = checked(value * 26 + (char.ToUpperInvariant(ch) - 'A' + 1));
        }

        return value - 1;
    }

    private static int FindHeader(IReadOnlyDictionary<string, int> headers, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (headers.TryGetValue(candidate, out var index))
                return index;
        }

        return -1;
    }
}
