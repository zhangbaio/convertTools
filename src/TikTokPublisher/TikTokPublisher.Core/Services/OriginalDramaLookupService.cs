using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.Networking;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed record OriginalDramaLookupRow(
    string NewTitle,
    string OriginalTitle,
    string Status,
    string Orientation,
    string Genre,
    string BookId,
    IReadOnlyList<HongguoDramaField> Fields,
    IReadOnlyList<string> Candidates)
{
    public static readonly string[] DetailNames =
    [
        "内容类型", "作者", "演员", "集数", "播放量", "收藏量", "时长", "上线时间", "备案号", "简介"
    ];

    public string Field(string name) =>
        Fields.FirstOrDefault(field => field.Name == name)?.Value ?? "";

    public string Format()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"新剧名：{NewTitle}");
        if (!string.IsNullOrWhiteSpace(OriginalTitle))
            builder.AppendLine($"原剧名：{OriginalTitle}");
        builder.AppendLine($"状态：{Status}");
        if (!string.IsNullOrWhiteSpace(Orientation))
            builder.AppendLine($"横竖屏：{Orientation}");
        if (!string.IsNullOrWhiteSpace(Genre))
            builder.AppendLine($"题材：{Genre}");
        foreach (var name in DetailNames)
        {
            var value = Field(name);
            if (!string.IsNullOrWhiteSpace(value))
                builder.AppendLine($"{name}：{value}");
        }

        if (BookId.Length == 0 && Candidates.Count > 0)
            builder.AppendLine("相近结果：" + string.Join("、", Candidates));
        return builder.ToString().TrimEnd();
    }
}

public static class OriginalDramaLookupService
{
    public const int SearchConcurrency = 4;

    public static async Task<IReadOnlyList<OriginalDramaLookupRow>> LookupAsync(
        IReadOnlyList<string> newTitles,
        string excelPath,
        ClientSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var titles = newTitles
            .Select(item => (item ?? "").Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var index = TikTokOriginalTitleIndex.Load(excelPath);
        var drama = DramaSourceSettingsMapping.FromClientSettings(settings) with
        {
            DramaSourceChain = "hghigh",
            HghighEdition = HongguoClientProfile.StandardEdition,
        };
        using var http = PreferredNetworkHttp.CreateClient(TimeSpan.FromMinutes(2));
        var api = new HongguoHighApiService(http);
        return await LookupCoreAsync(
            titles,
            index,
            SearchConcurrency,
            (originalTitle, token) => api.LookupExactTitleAsync(drama, originalTitle, token),
            progress,
            cancellationToken);
    }

    internal static async Task<IReadOnlyList<OriginalDramaLookupRow>> LookupCoreAsync(
        IReadOnlyList<string> titles,
        TikTokOriginalTitleIndex index,
        int concurrency,
        Func<string, CancellationToken, Task<HongguoDramaLookupHit>> search,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new OriginalDramaLookupRow?[titles.Count];
        var pending = new List<(int Index, string NewTitle, string OriginalTitle)>();
        for (var indexInBatch = 0; indexInBatch < titles.Count; indexInBatch++)
        {
            var newTitle = titles[indexInBatch];
            var matched = index.Find(newTitle);
            if (matched is null)
            {
                rows[indexInBatch] = new OriginalDramaLookupRow(newTitle, "", "未找到原剧名", "", "", "", [], []);
                continue;
            }

            pending.Add((indexInBatch, newTitle, matched.OriginalTitle));
        }

        var finished = new int[1];
        finished[0] = titles.Count - pending.Count;
        if (pending.Count == 0)
        {
            progress?.Report($"完成 {finished[0]}/{titles.Count}");
            return Materialize(rows);
        }

        progress?.Report($"正在并发查询 {pending.Count} 个原剧名");
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, SearchConcurrency));
        var tasks = pending.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                rows[item.Index] = await SearchOneAsync(item.NewTitle, item.OriginalTitle, search, cancellationToken);
            }
            finally
            {
                gate.Release();
                var done = Interlocked.Increment(ref finished[0]);
                progress?.Report($"已完成 {done}/{titles.Count}");
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return Materialize(rows);
    }

    private static async Task<OriginalDramaLookupRow> SearchOneAsync(
        string newTitle,
        string originalTitle,
        Func<string, CancellationToken, Task<HongguoDramaLookupHit>> search,
        CancellationToken cancellationToken)
    {
        try
        {
            var hit = await search(originalTitle, cancellationToken);
            if (string.IsNullOrWhiteSpace(hit.Title))
            {
                return new OriginalDramaLookupRow(
                    newTitle,
                    originalTitle,
                    "未找到同名剧集",
                    "",
                    "",
                    "",
                    [],
                    hit.Candidates);
            }

            return new OriginalDramaLookupRow(
                newTitle,
                originalTitle,
                "已找到",
                hit.Orientation,
                hit.Genre,
                hit.BookId,
                hit.Fields,
                hit.Candidates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OriginalDramaLookupRow(
                newTitle,
                originalTitle,
                "搜索失败：" + ex.Message,
                "",
                "",
                "",
                [],
                []);
        }
    }

    private static IReadOnlyList<OriginalDramaLookupRow> Materialize(OriginalDramaLookupRow?[] rows) =>
        rows.Select(row => row ?? throw new InvalidOperationException("查询结果缺失")).ToArray();

    public static void Export(Stream stream, IReadOnlyList<OriginalDramaLookupRow> rows)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"original-drama-{Guid.NewGuid():N}.xlsx");
        try
        {
            WriteWorkbook(tempPath, rows);
            using var file = File.OpenRead(tempPath);
            if (stream.CanSeek)
                stream.SetLength(0);
            file.CopyTo(stream);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void WriteWorkbook(string path, IReadOnlyList<OriginalDramaLookupRow> rows)
    {
        var headers = new List<string> { "新剧名", "原剧名", "状态", "横竖屏", "题材" };
        headers.AddRange(OriginalDramaLookupRow.DetailNames);
        headers.Add("相近结果");

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
            Name = "原剧查询",
        });

        sheetData.Append(CreateRow(1, headers));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var values = new List<string>
            {
                row.NewTitle,
                row.OriginalTitle,
                row.Status,
                row.Orientation,
                row.Genre,
            };
            foreach (var name in OriginalDramaLookupRow.DetailNames)
                values.Add(row.Field(name));
            values.Add(row.BookId.Length == 0 ? string.Join("、", row.Candidates) : "");
            sheetData.Append(CreateRow(index + 2, values));
        }

        workbookPart.Workbook.Save();
    }

    private static Row CreateRow(int rowIndex, IReadOnlyList<string> values)
    {
        var row = new Row { RowIndex = (uint)rowIndex };
        for (var index = 0; index < values.Count; index++)
        {
            row.Append(new Cell
            {
                CellReference = ColumnName(index) + rowIndex,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[index] ?? "") { Space = SpaceProcessingModeValues.Preserve }),
            });
        }

        return row;
    }

    private static string ColumnName(int index)
    {
        var value = index + 1;
        var name = "";
        while (value > 0)
        {
            value--;
            name = (char)('A' + value % 26) + name;
            value /= 26;
        }

        return name;
    }
}
