using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TikTokPublisher.Core.Services;

public enum TikTokCopyrightProofAuditState
{
    HasMaterial,
    ProductionAgreementOnly,
    PartialMaterial,
    MissingMaterial,
    SkippedApproved,
    SkippedUneditable,
    Failed,
}

public static class TikTokCopyrightProofAuditStateExtensions
{
    public static bool IsIncomplete(this TikTokCopyrightProofAuditState state) =>
        state is TikTokCopyrightProofAuditState.ProductionAgreementOnly
            or TikTokCopyrightProofAuditState.PartialMaterial
            or TikTokCopyrightProofAuditState.MissingMaterial;
}

public sealed record TikTokCopyrightProofAuditItem(
    int Order,
    string Title,
    string SeriesId,
    string DetailUrl,
    TikTokCopyrightProofAuditState State,
    string Detail,
    DateTimeOffset CheckedAt)
{
    public string PlatformStatus { get; init; } = string.Empty;
}

public static class TikTokCopyrightProofAuditText
{
    public static string BuildMissingTitlesCopyText(
        IEnumerable<TikTokCopyrightProofAuditItem> items) =>
        BuildTitleList(items, state => state.IsIncomplete());

    public static string BuildFailedTitlesCopyText(
        IEnumerable<TikTokCopyrightProofAuditItem> items) =>
        BuildTitleList(items, TikTokCopyrightProofAuditState.Failed);

    public static string BuildDisplayText(
        IEnumerable<TikTokCopyrightProofAuditItem> items)
    {
        var ordered = items
            .OrderBy(item => item.Order)
            .ToArray();
        var productionAgreementOnly = ordered
            .Where(item => item.State == TikTokCopyrightProofAuditState.ProductionAgreementOnly)
            .ToArray();
        var partial = ordered
            .Where(item => item.State == TikTokCopyrightProofAuditState.PartialMaterial)
            .ToArray();
        var missingAll = ordered
            .Where(item => item.State == TikTokCopyrightProofAuditState.MissingMaterial)
            .ToArray();
        var skipped = ordered
            .Where(item => item.State == TikTokCopyrightProofAuditState.SkippedUneditable)
            .ToArray();
        var approved = ordered
            .Where(item => item.State == TikTokCopyrightProofAuditState.SkippedApproved)
            .ToArray();
        var failed = ordered
            .Where(item => item.State == TikTokCopyrightProofAuditState.Failed)
            .ToArray();

        var sections = new List<string>();
        if (productionAgreementOnly.Length > 0)
        {
            sections.Add(
                $"【仅上传版权证明 PDF（{productionAgreementOnly.Length}）】{Environment.NewLine}" +
                string.Join(Environment.NewLine, productionAgreementOnly.Select(item => item.Title)));
        }

        if (partial.Length > 0)
        {
            sections.Add(
                $"【部分版权证明材料缺失（{partial.Length}）】{Environment.NewLine}" +
                string.Join(
                    Environment.NewLine,
                    partial.Select(item =>
                        string.IsNullOrWhiteSpace(item.Detail)
                            ? item.Title
                            : $"{item.Title}　[{item.Detail}]")));
        }

        if (missingAll.Length > 0)
        {
            sections.Add(
                $"【所有版权证明均未填写（{missingAll.Length}）】{Environment.NewLine}" +
                string.Join(Environment.NewLine, missingAll.Select(item => item.Title)));
        }

        if (skipped.Length > 0)
        {
            sections.Add(
                $"【暂不可编辑，已跳过（{skipped.Length}）】{Environment.NewLine}" +
                string.Join(
                    Environment.NewLine,
                    skipped.Select(item =>
                        string.IsNullOrWhiteSpace(item.Detail)
                            ? item.Title
                            : $"{item.Title}　[{item.Detail}]")));
        }

        if (approved.Length > 0)
        {
            sections.Add(
                $"【版权审核通过，已跳过（{approved.Length}）】{Environment.NewLine}" +
                string.Join(
                    Environment.NewLine,
                    approved.Select(item =>
                        string.IsNullOrWhiteSpace(item.Detail)
                            ? item.Title
                            : $"{item.Title}　[{item.Detail}]")));
        }

        if (failed.Length > 0)
        {
            sections.Add(
                $"【检查失败（{failed.Length}）】{Environment.NewLine}" +
                string.Join(
                    Environment.NewLine,
                    failed.Select(item =>
                        string.IsNullOrWhiteSpace(item.Detail)
                            ? item.Title
                            : $"{item.Title}　[{item.Detail}]")));
        }

        return sections.Count == 0
            ? "所选剧集均检测到版权证明材料。"
            : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static string BuildTitleList(
        IEnumerable<TikTokCopyrightProofAuditItem> items,
        TikTokCopyrightProofAuditState state) =>
        BuildTitleList(items, candidate => candidate == state);

    private static string BuildTitleList(
        IEnumerable<TikTokCopyrightProofAuditItem> items,
        Func<TikTokCopyrightProofAuditState, bool> predicate) =>
        string.Join(
            Environment.NewLine,
            items
                .Where(item => predicate(item.State))
                .OrderBy(item => item.Order)
                .Select(item => item.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.Ordinal));
}

public static class TikTokCopyrightProofAuditExcelService
{
    public static string Export(
        string accountName,
        IReadOnlyList<TikTokCopyrightProofAuditItem> items,
        string? outputPath = null)
    {
        if (items.Count == 0)
            throw new InvalidOperationException("没有可导出的版权证明检查结果。");

        outputPath ??= Path.Combine(
            AppPaths.DataRoot,
            "reports",
            $"{SafeFileName(accountName)}_版权证明检查_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var document = SpreadsheetDocument.Create(
            outputPath,
            SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(
            new Columns(
                new Column { Min = 1, Max = 1, Width = 8, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 42, CustomWidth = true },
                new Column { Min = 3, Max = 3, Width = 18, CustomWidth = true },
                new Column { Min = 4, Max = 4, Width = 16, CustomWidth = true },
                new Column { Min = 5, Max = 5, Width = 48, CustomWidth = true },
                new Column { Min = 6, Max = 6, Width = 24, CustomWidth = true },
                new Column { Min = 7, Max = 7, Width = 56, CustomWidth = true },
                new Column { Min = 8, Max = 8, Width = 24, CustomWidth = true }),
            sheetData);

        sheetData.Append(BuildRow(
            "序号",
            "剧集名称",
            "平台状态",
            "检查结果",
            "说明",
            "剧集 ID",
            "详情地址",
            "检查时间"));

        foreach (var item in items.OrderBy(item => item.Order))
        {
            sheetData.Append(BuildRow(
                item.Order.ToString(),
                item.Title,
                item.PlatformStatus,
                StateText(item.State),
                item.Detail,
                item.SeriesId,
                item.DetailUrl,
                item.CheckedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "版权证明检查",
        });
        workbookPart.Workbook.Save();
        return outputPath;
    }

    private static Row BuildRow(params string[] values)
    {
        var row = new Row();
        foreach (var value in values)
        {
            row.Append(new Cell
            {
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(value ?? string.Empty)),
            });
        }

        return row;
    }

    private static string StateText(TikTokCopyrightProofAuditState state) =>
        state switch
        {
            TikTokCopyrightProofAuditState.HasMaterial => "版权证明材料齐全",
            TikTokCopyrightProofAuditState.ProductionAgreementOnly => "仅上传版权证明 PDF",
            TikTokCopyrightProofAuditState.PartialMaterial => "部分版权证明材料缺失",
            TikTokCopyrightProofAuditState.MissingMaterial => "所有版权证明均未填写",
            TikTokCopyrightProofAuditState.SkippedApproved => "版权审核通过，已跳过",
            TikTokCopyrightProofAuditState.SkippedUneditable => "暂不可编辑，已跳过",
            _ => "检查失败",
        };

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(
                (value ?? "账号")
                .Select(character => invalid.Contains(character) ? '_' : character)
                .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(safe) ? "账号" : safe;
    }
}
