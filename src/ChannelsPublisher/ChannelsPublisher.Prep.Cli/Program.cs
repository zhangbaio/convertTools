using ChannelsPublisher.Prep;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("素材准备 CLI（C# 版 prep）");
    Console.WriteLine("用法: prep <prep-config.json>");
    Console.WriteLine("流程: [可选生成剪辑视频] → 扫描来源 → 原创度 → 封面 → AI描述 → 产出 <OutputDir>/publish-tasks.json");
    Console.WriteLine("生成剪辑：PrepConfig.GenerateClips=true 时，先把 ClipSourceDir 源视频简版切片到 material-clip-output/，条数/画质取自全局剪辑配置。");
    Console.WriteLine("产物由发布应用「导入任务(JSON)」直接消费。配置字段见 PrepConfig。");
    return 2;
}

PrepConfig cfg;
try { cfg = PrepConfig.Load(args[0]); }
catch (Exception ex) { Console.WriteLine($"读取配置失败：{ex.Message}"); return 1; }

var pipeline = new PrepPipeline();
try
{
    await pipeline.RunAsync(cfg, Console.WriteLine, CancellationToken.None);
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"prep 失败：{ex.GetType().Name}: {ex.Message}");
    return 1;
}
