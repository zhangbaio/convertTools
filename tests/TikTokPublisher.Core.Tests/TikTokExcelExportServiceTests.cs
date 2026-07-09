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
            var accountProfiles = new[]
            {
                new TikTokAccountProfile { Id = "acct-1", Name = "账号/一", TiktokLoginEmail = "tt-one@example.com" },
                new TikTokAccountProfile { Id = "acct-2", Name = "账号二", TiktokLoginEmail = "tt-two@example.com" },
            };
            var accountTwoWorkspace = Path.Combine(tempDir, "account-two-workspace");
            var workspaceByProject = items.ToDictionary(
                item => item.ProjectDir.Replace('\\', '/').ToLowerInvariant(),
                item => item.AccountProfileId == "acct-2" ? accountTwoWorkspace : tempDir);

            var exported = TikTokExcelExportService.Export(tempDir, items, account, new ClientSettings(), workspaceByProject, accountProfiles);

            exported.Should().Be(outputPath);
            using var document = SpreadsheetDocument.Open(exported, false);
            var workbookPart = document.WorkbookPart!;
            var sheetNames = workbookPart.Workbook.Sheets!.OfType<Sheet>()
                .Select(sheet => sheet.Name?.Value)
                .ToList();

            sheetNames.Should().Equal("汇总", "账号_一", "账号二");
            ReadColumn(workbookPart, "汇总", "原剧名").Should().Equal("剧一", "剧二", "剧三");
            ReadColumn(workbookPart, "汇总", "工作目录").Should().Equal(tempDir, accountTwoWorkspace, tempDir);
            ReadColumn(workbookPart, "汇总", "TIKTOK用户名").Should().Equal("tt-one@example.com", "tt-two@example.com", "tt-one@example.com");
            ReadHeaders(workbookPart, "汇总").Should().Contain("上传");
            ReadHeaders(workbookPart, "汇总").Should().NotContain(["账号ID", "下载", "改写", "海报", "小文件修复", "静音检测", "静音修复", "素材校验", "删源"]);
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
        var headers = ReadHeaders(workbookPart, sheetName);
        var columnIndex = headers.IndexOf(header);
        columnIndex.Should().BeGreaterThanOrEqualTo(0);
        return ReadRows(workbookPart, sheetName).Skip(1)
            .Select(row => ReadCell(row.Elements<Cell>().ElementAt(columnIndex)))
            .ToList();
    }

    private static List<string> ReadHeaders(WorkbookPart workbookPart, string sheetName)
    {
        var rows = ReadRows(workbookPart, sheetName);
        rows.Should().NotBeEmpty();
        return rows[0].Elements<Cell>().Select(ReadCell).ToList();
    }

    private static IReadOnlyList<Row> ReadRows(WorkbookPart workbookPart, string sheetName)
    {
        var sheet = workbookPart.Workbook.Sheets!.OfType<Sheet>()
            .Single(sheet => string.Equals(sheet.Name?.Value, sheetName, StringComparison.Ordinal));
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        var rows = worksheetPart.Worksheet.Descendants<Row>().ToList();
        rows.Should().NotBeEmpty();
        return rows;
    }

    private static string ReadCell(Cell cell) => cell.CellValue?.Text ?? "";
}
