using ChannelsPublisher.Core.Publishing;

namespace ChannelsPublisher.Prep;

/// <summary>素材准备流水线：扫描来源 → 逐条(原创度→封面→AI描述) → 产出 publish-tasks.json。
/// 输出的 JSON 契约由 .NET 发布应用「导入任务」直接消费（P3）。</summary>
public sealed class PrepPipeline
{
    private readonly SourceScanner _scanner = new();
    private readonly FfmpegOriginalityProcessor _originality = new();
    private readonly CoverResolver _cover = new();
    private readonly AiDescriptionService _ai = new();

    public async Task<PublishTaskFile> RunAsync(PrepConfig cfg, Action<string>? log, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);

        var outDir = string.IsNullOrWhiteSpace(cfg.OutputDir)
            ? Path.Combine(cfg.SourceDir, "_prep_output")
            : cfg.OutputDir;
        Directory.CreateDirectory(outDir);

        var materials = _scanner.Scan(cfg);
        L($"来源[{cfg.SourceType}] 扫描到 {materials.Count} 个视频");

        var file = new PublishTaskFile { FinalAction = cfg.FinalAction };
        int i = 0;
        foreach (var m in materials)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            L($"[{i}/{materials.Count}] {Path.GetFileName(m.VideoPath)}");

            // 1) 原创度（种子=文件名，确定性）
            string videoOut = m.VideoPath;
            if (cfg.OriginalityEnabled)
            {
                var plan = OriginalityPlanBuilder.Build(cfg, Path.GetFileName(m.VideoPath), cfg.Width, cfg.Height);
                if (!plan.IsEmpty)
                {
                    var candidate = Path.Combine(outDir, Path.GetFileNameWithoutExtension(m.VideoPath) + ".orig.mp4");
                    try { videoOut = await _originality.ProcessAsync(m.VideoPath, candidate, plan, cfg.FfmpegPath, ct); L("  ✓ 原创度处理"); }
                    catch (Exception ex) { L($"  ⚠ 原创度失败，用原片：{ex.Message}"); videoOut = m.VideoPath; }
                }
            }

            // 2) 封面（来源自带封面优先，如系统高光的 <stem>.cover.jpg）
            string? cover = m.CoverPath;
            if (cover != null) L("  ✓ 封面(来源自带)");
            else
            {
                try { cover = await _cover.ResolveAsync(m.VideoPath, cfg, outDir, ct); if (cover != null) L("  ✓ 封面"); }
                catch (Exception ex) { L($"  ⚠ 封面失败：{ex.Message}"); }
            }

            // 3) AI 描述（失败回退基础文案）
            var baseDesc = string.IsNullOrWhiteSpace(m.BaseDescription) ? cfg.DescriptionTemplate : m.BaseDescription;
            string description = baseDesc;
            if (cfg.AiEnabled)
            {
                try { description = await _ai.GenerateAsync(cfg, m.Title, baseDesc, ct); L("  ✓ AI 描述"); }
                catch (Exception ex) { L($"  ⚠ AI 描述失败，用原文案：{ex.Message}"); description = baseDesc; }
            }

            // 挂载剧集：显式 cfg.DramaName 优先，否则用来源自带剧名（新剧挂载/系统高光的 manifest 剧名）
            var dramaName = !string.IsNullOrWhiteSpace(cfg.DramaName) ? cfg.DramaName : m.DramaTitle;

            file.Tasks.Add(new PublishTaskDto
            {
                Account = cfg.Account,
                VideoPath = videoOut,
                Description = description,
                ShortTitle = "",
                CoverPath = cover,
                DramaName = dramaName,
                DeclareOriginal = cfg.DeclareOriginal,
            });
        }

        var outFile = Path.Combine(outDir, "publish-tasks.json");
        file.Save(outFile);
        L($"已生成 {file.Tasks.Count} 条发布任务 → {outFile}");
        return file;
    }
}
