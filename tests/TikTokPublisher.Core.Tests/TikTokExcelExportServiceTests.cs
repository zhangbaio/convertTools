using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokExcelExportServiceTests
{
    [Fact]
    public void Export_Creates_Summary_And_Per_Account_Sheets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tiktok-excel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var outputPath = Path.Combine(tempDir, "records.xlsx");
            var account = new TikTokAccountProfile { TiktokExcelReportPath = outputPath };
            var items = new[]
            {
                BuildItem(tempDir, "project-a", "剧一", "acct-1", "账号/一"),
                BuildItem(tempDir, "project-b", "剧二", "acct-2", "账号二"),
                BuildItem(tempDir, "project-c", "剧三", "acct-1", "账号/一"),
            };

            var exported = TikTokExcelExportService.Export(tempDir, items, account, new ClientSettings());

            exported.Should().Be(outputPath);
            using var document = SpreadsheetDocument.Open(exported, false);
            var workbookPart = document.WorkbookPart!;
            var sheetNames = workbookPart.Workbook.Sheets!.OfType<Sheet>()
                .Select(sheet => sheet.Name?.Value)
                .ToList();

            sheetNames.Should().Equal("汇总", "账号_一", "账号二", "执行流水");
            ReadColumn(workbookPart, "汇总", "原剧名").Should().Equal("剧一", "剧二", "剧三");
            ReadColumn(workbookPart, "账号_一", "原剧名").Should().Equal("剧一", "剧三");
            ReadColumn(workbookPart, "账号二", "原剧名").Should().Equal("剧二");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static QueueProjectItem BuildItem(
        string root,
        string folderName,
        string title,
        string accountId,
        string accountName)
    {
        var projectDir = Path.Combine(root, folderName);
        Directory.CreateDirectory(projectDir);

        return new QueueProjectItem
        {
            ProjectDir = projectDir,
            DisplayName = title,
            OriginalTitle = title,
            NewTitle = $"{title}-新",
            EpisodeCount = 12,
            AccountProfileId = accountId,
            AccountProfileName = accountName,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
            },
        };
    }

    private static IReadOnlyList<string> ReadColumn(WorkbookPart workbookPart, string sheetName, string header)
    {
        var sheet = workbookPart.Workbook.Sheets!.OfType<Sheet>()
            .Single(sheet => string.Equals(sheet.Name?.Value, sheetName, StringComparison.Ordinal));
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        var rows = worksheetPart.Worksheet.Descendants<Row>().ToList();
        rows.Should().NotBeEmpty();

        var headers = rows[0].Elements<Cell>().Select(ReadCell).ToList();
        var columnIndex = headers.IndexOf(header);
        columnIndex.Should().BeGreaterThanOrEqualTo(0);

        return rows.Skip(1)
            .Select(row => ReadCell(row.Elements<Cell>().ElementAt(columnIndex)))
            .ToList();
    }

    private static string ReadCell(Cell cell) => cell.CellValue?.Text ?? "";
}
