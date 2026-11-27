using System.Globalization;
using System.Text.Json;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Imaging;
using static PlatformPublisher.Weixin.Publishing.WeixinSeriesFields;

namespace PlatformPublisher.Weixin.Publishing;

internal sealed record WeixinSeriesFormReportInputs(string ProjectDirectory, string SourceDirectory, string Title,
    string Producer, decimal Cost, int Total, string Ratio, string Representative, string Seal, string Signature);

internal static class WeixinSeriesFormReport
{
    public static WeixinSeriesFormReportInputs? Resolve(Func<string, string> field, bool usePermit,
        string projectDirectory, string sourceDirectory, List<string> errors)
    {
        if (usePermit || field(GenerateCost) != "启用")
            return null;
        var seal = WeixinSeriesFormService.ReadPaths(field(Seal), sourceDirectory, Seal, errors).FirstOrDefault();
        var signature = WeixinSeriesFormService.ReadPaths(field(Signature), sourceDirectory, Signature, errors).FirstOrDefault();
        if (seal is null || signature is null)
            errors.Add("生成成本报表需要有效的公章和法定代表人签名图片。");
        var ratio = field(Ratio).TrimEnd('%', '％').Trim();
        if (ratio.Length > 0 &&
            (!decimal.TryParse(ratio, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 100))
            errors.Add("演员总片酬占比须为 0–100。");
        decimal.TryParse(field(Cost), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost);
        int.TryParse(field(Total), out var total);
        return new(projectDirectory, sourceDirectory, field(Title), field(Producer), cost, total, ratio,
            field(Representative), seal ?? string.Empty, signature ?? string.Empty);
    }

    public static async Task<string> RenderAsync(IProjectInfoParser parser, WeixinSeriesFormReportInputs inputs,
        string jobDirectory, CancellationToken ct)
    {
        var projectDirectory = File.Exists(Path.Combine(inputs.SourceDirectory, "短剧信息.txt")) ||
            File.Exists(Path.Combine(inputs.SourceDirectory, "shortdrama-project.json"))
            ? inputs.SourceDirectory : inputs.ProjectDirectory;
        var project = await parser.ParseAsync(projectDirectory, ct);
        project = project with { Title = inputs.Title, CompanyName = inputs.Producer, CostAmountWan = inputs.Cost,
            EpisodeCount = inputs.Total };
        var assets = Path.Combine(jobDirectory, "report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(assets);
        try
        {
            File.Copy(inputs.Seal, Path.Combine(assets, "seal.png"));
            File.Copy(inputs.Signature, Path.Combine(assets, "sign.png"));
            var config = new Dictionary<string, string>
            {
                ["CostReportLegalRepresentative"] = inputs.Representative,
                ["CostReportActorPayRatio"] = inputs.Ratio.Length == 0 ? "" : inputs.Ratio + "%",
            };
            foreach (var (name, key) in new[] { ("cost-report-base.png", "CostReportBaseImagePath"),
                         ("cost-report-layout.json", "CostReportLayoutPath") })
            {
                var path = Path.Combine(inputs.SourceDirectory, name);
                if (File.Exists(path)) config[key] = path;
            }
            await File.WriteAllTextAsync(Path.Combine(assets, "config.json"), JsonSerializer.Serialize(config), ct);
            var output = Path.Combine(assets, "成本报表.png");
            await CostReportImageRenderer.RenderAsync(
                new CostReportBuildRequest(string.Empty, projectDirectory, assets, jobDirectory, project), output, ct);
            var destination = Path.Combine(jobDirectory, "成本报表.png");
            if (!File.Exists(destination) || !File.ReadAllBytes(output).SequenceEqual(File.ReadAllBytes(destination)))
                File.Move(output, destination, overwrite: true);
            return destination;
        }
        finally
        {
            Directory.Delete(assets, recursive: true);
        }
    }
}
