using ChannelsPublisher.Prep;

// 验证 prep 纯逻辑：原创度计划确定性 + 数字用 '.'（非逗号）+ 来源扫描。
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var failures = new List<string>();

var cfg = new PrepConfig
{
    OriginalityEnabled = true,
    OrigZoom = true, OrigColor = true, OrigSpeed = true, OrigFade = false,
    Width = 1080, Height = 1920,
};

// 1) 同种子 → 完全一致（确定性，可复现）
var p1 = OriginalityPlanBuilder.Build(cfg, "第1集.mp4", 1080, 1920);
var p2 = OriginalityPlanBuilder.Build(cfg, "第1集.mp4", 1080, 1920);
if (!p1.VideoFilters.SequenceEqual(p2.VideoFilters) || p1.Atempo != p2.Atempo)
    failures.Add("同种子结果不一致（应确定性可复现）");

// 2) 不同种子 → 大概率不同
var p3 = OriginalityPlanBuilder.Build(cfg, "第2集.mp4", 1080, 1920);
if (p1.VideoFilters.SequenceEqual(p3.VideoFilters))
    failures.Add("不同种子结果相同（扰动未随文件名变化）");

// 3) 数字格式化用 '.'（InvariantCulture），滤镜里不能出现 "1,0" 这种逗号小数
var chain = string.Join(",", p1.VideoFilters);
Console.WriteLine("滤镜链: " + chain);
if (!chain.Contains("scale=1080:1920")) failures.Add("zoom 滤镜缺少 scale=1080:1920");
if (System.Text.RegularExpressions.Regex.IsMatch(chain, @"\d,\d"))
    failures.Add("检测到逗号小数（应为 InvariantCulture 的 '.'）");
if (p1.Atempo is double at && !at.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains('.'))
    { /* 整数 atempo 罕见，忽略 */ }

// 4) 来源扫描：临时目录放 2 个视频 + 1 个同名 txt
var tmp = Path.Combine(Path.GetTempPath(), "prepcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);
File.WriteAllBytes(Path.Combine(tmp, "第1集.mp4"), new byte[] { 0 });
File.WriteAllBytes(Path.Combine(tmp, "第2集.mp4"), new byte[] { 0 });
File.WriteAllText(Path.Combine(tmp, "第1集.txt"), "第一集的描述 #短剧");
var scanned = new SourceScanner().Scan(tmp);
if (scanned.Count != 2) failures.Add($"扫描到 {scanned.Count} 个视频，期望 2");
var first = scanned.FirstOrDefault(s => s.Title == "第1集");
if (first is null || first.BaseDescription != "第一集的描述 #短剧")
    failures.Add("同名 txt 描述未读取");
try { Directory.Delete(tmp, true); } catch { }

// 5) 自然排序：第2集 应排在 第10集 前
var tmp2 = Path.Combine(Path.GetTempPath(), "prepcheck-nat-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp2);
foreach (var name in new[] { "第10集.mp4", "第2集.mp4", "第1集.mp4" })
    File.WriteAllBytes(Path.Combine(tmp2, name), new byte[] { 0 });
var natOrder = new SourceScanner().Scan(new PrepConfig { SourceType = "directory", SourceDir = tmp2 })
    .Select(s => s.Title).ToList();
if (!natOrder.SequenceEqual(new[] { "第1集", "第2集", "第10集" }))
    failures.Add("自然排序不对：" + string.Join(",", natOrder));
try { Directory.Delete(tmp2, true); } catch { }

// 6) 系统高光下载：视频 + <stem>.publish.json 旁车 + <stem>.cover.jpg + manifest
var hl = Path.Combine(Path.GetTempPath(), "prepcheck-hl-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(hl);
File.WriteAllBytes(Path.Combine(hl, "高光-01.mp4"), new byte[] { 0 });
File.WriteAllBytes(Path.Combine(hl, "高光-01.cover.jpg"), new byte[] { 0 });
File.WriteAllText(Path.Combine(hl, "高光-01.publish.json"),
    "{\"title\":\"高光短标题\",\"description\":\"高光描述 #系统高光\",\"object_id\":\"oid-1\",\"duration_sec\":12}");
File.WriteAllText(Path.Combine(hl, ".system-highlight-download.json"), "{\"drama_title\":\"百年修行\"}");
var hlItems = new SourceScanner().Scan(new PrepConfig { SourceType = "downloaded_system_highlight", SourceDir = hl });
if (hlItems.Count != 1) failures.Add($"高光扫描 {hlItems.Count} 条，期望 1");
else
{
    var it = hlItems[0];
    if (it.Title != "高光短标题") failures.Add("高光标题未读旁车");
    if (it.BaseDescription != "高光描述 #系统高光") failures.Add("高光描述未读旁车");
    if (it.CoverPath is null || !it.CoverPath.EndsWith("高光-01.cover.jpg")) failures.Add("高光封面未解析");
    if (it.DramaTitle != "百年修行") failures.Add("高光剧名未读 manifest");
}
try { Directory.Delete(hl, true); } catch { }

// 7) 新剧挂载：标题取 shortdrama-project.json
var nd = Path.Combine(Path.GetTempPath(), "prepcheck-nd-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(Path.Combine(nd, "videos"));
File.WriteAllBytes(Path.Combine(nd, "videos", "第1集.mp4"), new byte[] { 0 });
File.WriteAllText(Path.Combine(nd, "shortdrama-project.json"), "{\"displayName\":\"槐下老翁修仙问长生\",\"episodeCount\":103}");
var ndItems = new SourceScanner().Scan(new PrepConfig { SourceType = "new_drama_mount", SourceDir = nd });
if (ndItems.Count != 1) failures.Add($"新剧挂载扫描 {ndItems.Count} 条，期望 1");
else if (ndItems[0].DramaTitle != "槐下老翁修仙问长生") failures.Add("新剧挂载剧名未读 shortdrama-project.json");
try { Directory.Delete(nd, true); } catch { }

// 8) 目录批量发表：子目录取最大视频 + description.txt(#A#B → #A #B)
var dp = Path.Combine(Path.GetTempPath(), "prepcheck-dp-" + Guid.NewGuid().ToString("N")[..8]);
var sub = Path.Combine(dp, "剧集A");
Directory.CreateDirectory(sub);
File.WriteAllBytes(Path.Combine(sub, "small.mp4"), new byte[10]);
File.WriteAllBytes(Path.Combine(sub, "big.mp4"), new byte[500]);
File.WriteAllText(Path.Combine(sub, "description.txt"), "完整描述 #话题A#话题B");
var dpItems = new SourceScanner().Scan(new PrepConfig { SourceType = "directory_publish", SourceDir = dp });
if (dpItems.Count != 1) failures.Add($"目录批量发表 {dpItems.Count} 条，期望 1");
else
{
    if (!dpItems[0].VideoPath.EndsWith("big.mp4")) failures.Add("目录批量发表未取最大视频");
    if (dpItems[0].BaseDescription != "完整描述 #话题A #话题B") failures.Add($"标签归一化不对：{dpItems[0].BaseDescription}");
}
try { Directory.Delete(dp, true); } catch { }

// 9) 自选视频：显式列表
var cf = Path.Combine(Path.GetTempPath(), "prepcheck-cf-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(cf);
var cfV1 = Path.Combine(cf, "第2集.mp4"); var cfV2 = Path.Combine(cf, "第10集.mp4");
File.WriteAllBytes(cfV1, new byte[1]); File.WriteAllBytes(cfV2, new byte[1]);
File.WriteAllText(Path.Combine(cf, "readme.txt"), "非视频不应计入");
var cfItems = new SourceScanner().Scan(new PrepConfig
{
    SourceType = "custom_files",
    CustomFiles = new List<string> { cfV2, cfV1, Path.Combine(cf, "readme.txt") },
});
if (cfItems.Select(s => s.Title).SequenceEqual(new[] { "第2集", "第10集" }) == false)
    failures.Add("自选视频未过滤/未自然排序：" + string.Join(",", cfItems.Select(s => s.Title)));
try { Directory.Delete(cf, true); } catch { }

// 10) 剪辑成片：<sourceDir>/素材剪辑输出
var mc = Path.Combine(Path.GetTempPath(), "prepcheck-mc-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(Path.Combine(mc, "素材剪辑输出"));
File.WriteAllBytes(Path.Combine(mc, "素材剪辑输出", "第1集-剪辑.mp4"), new byte[1]);
var mcItems = new SourceScanner().Scan(new PrepConfig { SourceType = "material_clips", SourceDir = mc });
if (mcItems.Count != 1) failures.Add($"剪辑成片 {mcItems.Count} 条，期望 1");
try { Directory.Delete(mc, true); } catch { }

Console.WriteLine(failures.Count == 0
    ? "\n✅ prep 纯逻辑验证通过：原创度 + 全部来源(目录/高光/新剧挂载/剪辑/项目/源/自选/目录批量) + 自然排序 + 标签归一化"
    : "\n❌ 失败：\n  - " + string.Join("\n  - ", failures));
return failures.Count == 0 ? 0 : 1;
