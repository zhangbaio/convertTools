using System.Globalization;
using System.Text;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

public static class VideoDedupEffectFilters
{
    public static void Validate(VideoDedupPreset preset)
    {
        var effects = preset.Effects ?? new VideoDedupEffects();
        if (effects.LutAmount > 0.01 && (string.IsNullOrWhiteSpace(effects.LutPath) || !File.Exists(effects.LutPath)))
            throw new InvalidOperationException($"画面滤镜的 3D LUT 文件不存在：{effects.LutPath}");
        foreach (var layer in effects.Layers.Where(IsVisibleImage))
        {
            if (string.IsNullOrWhiteSpace(layer.Path) || !File.Exists(layer.Path))
                throw new InvalidOperationException($"画面滤镜的图片不存在：{layer.Path}");
        }
    }

    public static IReadOnlyList<string> ImageInputs(VideoDedupPreset preset) =>
        (preset.Effects?.Layers ?? []).Where(IsVisibleImage).Select(layer => layer.Path).ToArray();

    public static bool NeedsGraph(VideoDedupPreset preset)
    {
        var effects = preset.Effects;
        if (effects is null)
            return false;
        if (effects.LutAmount > 0.01 && effects.LutAmount < 99.9 && !string.IsNullOrWhiteSpace(effects.LutPath))
            return true;
        if (effects.MosaicAmount > 0.01 && effects.MosaicRegions.Any(region => region.Width > 0 && region.Height > 0))
            return true;
        return effects.Layers.Any(IsVisibleImage);
    }

    public static string LinearFilters(VideoDedupPreset preset)
    {
        var effects = preset.Effects ?? new VideoDedupEffects();
        var filters = new List<string>();
        if (Math.Abs(effects.Exposure) > 0.01)
            filters.Add($"exposure=exposure={Num(effects.Exposure / 50d)}");

        var gamma = 1 + effects.Gamma / 200d;
        var eq = new List<string>();
        if (Math.Abs(preset.Brightness) > 0.0001)
            eq.Add($"brightness={Num(preset.Brightness)}");
        if (Math.Abs(preset.Contrast - 1) > 0.0001)
            eq.Add($"contrast={Num(preset.Contrast)}");
        if (Math.Abs(preset.Saturation - 1) > 0.0001)
            eq.Add($"saturation={Num(preset.Saturation)}");
        if (Math.Abs(gamma - 1) > 0.0001)
            eq.Add($"gamma={Num(gamma)}");
        if (eq.Count > 0)
            filters.Add("eq=" + string.Join(':', eq));

        if (Math.Abs(effects.Temperature) > 0.01)
        {
            var warm = effects.Temperature / 200d;
            filters.Add($"colorbalance=rm={Num(warm)}:bm={Num(-warm)}");
        }

        if (Math.Abs(effects.Tint) > 0.01)
            filters.Add($"colorbalance=gm={Num(effects.Tint / 100d)}");
        if (Math.Abs(effects.Vibrance) > 0.01)
            filters.Add($"vibrance=intensity={Num(effects.Vibrance / 100d)}");
        if (preset.Grayscale)
            filters.Add("hue=s=0");
        if (preset.Sharpness > 0.01)
            filters.Add($"unsharp=5:5:{Num(preset.Sharpness)}");
        else if (preset.Sharpness < -0.01)
            filters.Add($"gblur=sigma={Num(Math.Abs(preset.Sharpness))}");

        AddCurves(filters, effects);
        AddLevels(filters, effects);
        AddSplitTone(filters, effects);
        if (effects.LutAmount >= 99.9 && !string.IsNullOrWhiteSpace(effects.LutPath))
            filters.Add($"lut3d=file='{EscapePath(effects.LutPath)}'");
        if (effects.Blur > 0.01)
            filters.Add($"gblur=sigma={Num(effects.Blur)}");
        if (effects.VignetteAmount > 0.01)
        {
            var angle = Math.PI / 5d
                * (1 + (100 - effects.VignetteAmount) / 35d)
                * (effects.VignetteRadius / 70d)
                * (1 + effects.VignetteFeather / 200d);
            filters.Add($"vignette=angle={Num(angle)}");
        }

        if (effects.GrainAmount > 0.01)
        {
            var strength = effects.GrainAmount / 100d * Math.Max(effects.GrainDensity, 1) / 100d * effects.GrainSize;
            filters.Add($"noise=alls={Num(Math.Max(1, strength))}:allf=t+u");
        }

        foreach (var layer in effects.Layers)
        {
            if (layer.Kind == VideoDedupOverlayLayer.KindText && !string.IsNullOrWhiteSpace(layer.Text) && layer.Opacity > 0.01)
                filters.Add(BuildText(layer));
            else if (layer.Kind == VideoDedupOverlayLayer.KindBorder && layer.BorderPercent > 0.01)
                filters.Add(BuildBorder(layer));
        }

        return string.Join(",", filters);
    }

    public static string Finish(StringBuilder graph, string inputLabel, VideoDedupPreset preset, int imageInputIndex)
    {
        var current = inputLabel;
        var linear = LinearFilters(preset);
        if (linear.Length > 0)
        {
            Separate(graph);
            graph.Append('[').Append(current).Append(']').Append(linear).Append("[veffect]");
            current = "veffect";
        }

        if (NeedsGraph(preset))
            current = AppendGraph(graph, current, preset, imageInputIndex);
        return current;
    }

    public static IReadOnlyList<string> Describe(VideoDedupPreset preset)
    {
        var effects = preset.Effects ?? new VideoDedupEffects();
        var names = new List<string>();
        if (Math.Abs(effects.Temperature) > 0.01) names.Add($"色温 {Num(effects.Temperature)}");
        if (Math.Abs(effects.Tint) > 0.01) names.Add($"色调 {Num(effects.Tint)}");
        if (Math.Abs(preset.Saturation - 1) > 0.0001) names.Add($"饱和度 {Num(preset.Saturation)}");
        if (Math.Abs(effects.Vibrance) > 0.01) names.Add($"自然饱和度 {Num(effects.Vibrance)}");
        if (Math.Abs(preset.Brightness) > 0.0001) names.Add($"亮度 {Num(preset.Brightness)}");
        if (Math.Abs(preset.Contrast - 1) > 0.0001) names.Add($"对比度 {Num(preset.Contrast)}");
        if (Math.Abs(effects.Exposure) > 0.01) names.Add($"曝光 {Num(effects.Exposure)}");
        if (Math.Abs(effects.Gamma) > 0.01) names.Add($"中间调 {Num(effects.Gamma)}");
        if (preset.Sharpness > 0.01) names.Add($"锐化 {Num(preset.Sharpness)}");
        else if (preset.Sharpness < -0.01) names.Add($"柔化 {Num(preset.Sharpness)}");
        if (preset.Grayscale) names.Add("灰度");
        if (effects.CurveAmount > 0.01 && !IsIdentity(effects)) names.Add($"曲线 {Num(effects.CurveAmount)}%");
        if (effects.LevelsAmount > 0.01) names.Add($"色阶 {Num(effects.LevelsAmount)}%");
        if (effects.SplitToneAmount > 0.01) names.Add($"分离色调 {Num(effects.SplitToneAmount)}%");
        if (effects.LutAmount > 0.01 && !string.IsNullOrWhiteSpace(effects.LutPath)) names.Add($"3D LUT {Num(effects.LutAmount)}%");
        if (effects.Blur > 0.01) names.Add($"模糊 {Num(effects.Blur)}");
        if (effects.VignetteAmount > 0.01) names.Add($"暗角 {Num(effects.VignetteAmount)}%");
        if (effects.GrainAmount > 0.01) names.Add($"颗粒 {Num(effects.GrainAmount)}%");
        if (effects.MosaicAmount > 0.01 && effects.MosaicRegions.Count > 0) names.Add($"局部马赛克 {effects.MosaicRegions.Count.ToString(CultureInfo.InvariantCulture)} 处");
        var images = effects.Layers.Count(layer => layer.Kind == VideoDedupOverlayLayer.KindImage && layer.Opacity > 0.01);
        var texts = effects.Layers.Count(layer => layer.Kind == VideoDedupOverlayLayer.KindText && !string.IsNullOrWhiteSpace(layer.Text) && layer.Opacity > 0.01);
        var borders = effects.Layers.Count(layer => layer.Kind == VideoDedupOverlayLayer.KindBorder && layer.BorderPercent > 0.01);
        if (images > 0) names.Add($"图片 {images.ToString(CultureInfo.InvariantCulture)} 层");
        if (texts > 0) names.Add($"文字 {texts.ToString(CultureInfo.InvariantCulture)} 层");
        if (borders > 0) names.Add($"边框 {borders.ToString(CultureInfo.InvariantCulture)} 层");
        return names;
    }

    public static string Summary(VideoDedupPreset preset)
    {
        var names = Describe(preset);
        return names.Count == 0
            ? "未启用。数值为 0 时不参与去重。"
            : "已启用：" + string.Join("、", names) + "。保存账号配置后，下次去重生效。";
    }

    public static async Task RenderPreviewAsync(
        VideoDedupPreset preset,
        string videoPath,
        double seconds,
        string originalPath,
        string filteredPath,
        CancellationToken ct)
    {
        Validate(preset);
        var ffmpeg = FfmpegLocator.ResolveFfmpeg();
        var position = Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
        await FfmpegRunner.RunAsync(
            ffmpeg,
            ["-y", "-hide_banner", "-loglevel", "error", "-ss", position, "-i", videoPath, "-frames:v", "1", "-vf", "scale=640:-2", originalPath],
            ct).ConfigureAwait(false);

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-ss", position, "-i", videoPath };
        foreach (var image in ImageInputs(preset))
            args.AddRange(["-i", image]);
        var graph = new StringBuilder("[0:v]scale=640:-2[vprev]");
        var label = Finish(graph, "vprev", preset, 1);
        args.AddRange(["-filter_complex", graph.ToString(), "-map", $"[{label}]", "-frames:v", "1", filteredPath]);
        await FfmpegRunner.RunAsync(ffmpeg, args, ct).ConfigureAwait(false);
    }

    private static string AppendGraph(StringBuilder graph, string input, VideoDedupPreset preset, int imageInputIndex)
    {
        var effects = preset.Effects ?? new VideoDedupEffects();
        var current = input;
        var sequence = 0;
        string Next() => "fx" + (sequence++).ToString(CultureInfo.InvariantCulture);

        if (effects.LutAmount > 0.01 && effects.LutAmount < 99.9 && !string.IsNullOrWhiteSpace(effects.LutPath))
        {
            var left = Next();
            var right = Next();
            var graded = Next();
            var output = Next();
            Link(graph, $"[{current}]split[{left}][{right}]");
            Link(graph, $"[{right}]lut3d=file='{EscapePath(effects.LutPath)}'[{graded}]");
            Link(graph, $"[{left}][{graded}]blend=all_opacity={Num(effects.LutAmount / 100d)}[{output}]");
            current = output;
        }

        if (effects.MosaicAmount > 0.01)
        {
            foreach (var region in effects.MosaicRegions.Where(region => region.Width > 0 && region.Height > 0))
            {
                var baseLabel = Next();
                var cut = Next();
                var pix = Next();
                var output = Next();
                var width = Num(region.Width / 100d);
                var height = Num(region.Height / 100d);
                var x = Num(region.X / 100d);
                var y = Num(region.Y / 100d);
                var block = Math.Clamp(region.Block, 2, 64).ToString(CultureInfo.InvariantCulture);
                Link(graph, $"[{current}]split[{baseLabel}][{cut}]");
                Link(graph, $"[{cut}]crop=w=iw*{width}:h=ih*{height}:x=iw*{x}:y=ih*{y},scale=w='max(2,trunc(iw/{block}))':h='max(2,trunc(ih/{block}))':flags=neighbor,scale=w='trunc(iw*{block})':h='trunc(ih*{block})':flags=neighbor,format=rgba,colorchannelmixer=aa={Num(effects.MosaicAmount / 100d)}[{pix}]");
                Link(graph, $"[{baseLabel}][{pix}]overlay=x='main_w*{x}':y='main_h*{y}':format=auto[{output}]");
                current = output;
            }
        }

        var imageIndex = imageInputIndex;
        foreach (var layer in effects.Layers.Where(IsVisibleImage))
        {
            var scaled = Next();
            var main = Next();
            var colored = Next();
            var output = Next();
            var width = Num(Math.Max(layer.Width, 1) / 100d);
            var height = Num(Math.Max(layer.Height, 1) / 100d);
            Link(graph, $"[{imageIndex.ToString(CultureInfo.InvariantCulture)}:v][{current}]scale2ref=w='main_w*{width}':h='main_h*{height}'[{scaled}][{main}]");
            Link(graph, $"[{scaled}]format=rgba,colorchannelmixer=aa={Num(layer.Opacity / 100d)}[{colored}]");
            Link(graph, $"[{main}][{colored}]overlay=x='main_w*{Num(layer.X / 100d)}':y='main_h*{Num(layer.Y / 100d)}':format=auto[{output}]");
            current = output;
            imageIndex++;
        }

        return current;
    }

    private static void AddCurves(List<string> filters, VideoDedupEffects effects)
    {
        if (effects.CurveAmount <= 0.01)
            return;
        var master = CurveSpec(effects.CurveMaster, effects.CurveAmount);
        var red = CurveSpec(effects.CurveRed, effects.CurveAmount);
        var green = CurveSpec(effects.CurveGreen, effects.CurveAmount);
        var blue = CurveSpec(effects.CurveBlue, effects.CurveAmount);
        if (IsStraight(master) && IsStraight(red) && IsStraight(green) && IsStraight(blue))
            return;
        filters.Add($"curves=m='{master}':r='{red}':g='{green}':b='{blue}'");
    }

    private static string CurveSpec(IReadOnlyList<VideoDedupCurvePoint> points, double amount)
    {
        var parts = new List<string>();
        foreach (var point in points.OrderBy(point => point.X))
        {
            var y = point.X + (point.Y - point.X) * amount / 100d;
            parts.Add($"{Num(point.X)}/{Num(Math.Clamp(y, 0, 1))}");
        }

        return string.Join(' ', parts);
    }

    private static bool IsStraight(string spec) =>
        spec.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(part =>
            {
                var pair = part.Split('/');
                return pair.Length == 2 && pair[0] == pair[1];
            });

    private static void AddLevels(List<string> filters, VideoDedupEffects effects)
    {
        if (effects.LevelsAmount <= 0.01)
            return;
        var amount = effects.LevelsAmount / 100d;
        var inputBlack = effects.InputBlack * amount;
        var inputWhite = 1 + (effects.InputWhite - 1) * amount;
        var outputBlack = effects.OutputBlack * amount;
        var outputWhite = 1 + (effects.OutputWhite - 1) * amount;
        var gamma = 1 + (effects.LevelsGamma - 1) * amount;
        if (inputWhite <= inputBlack)
            inputWhite = inputBlack + 0.01;
        var changed = Math.Abs(inputBlack) > 0.0001 || Math.Abs(inputWhite - 1) > 0.0001 ||
                      Math.Abs(outputBlack) > 0.0001 || Math.Abs(outputWhite - 1) > 0.0001;
        if (changed)
        {
            filters.Add(
                "colorlevels=" +
                $"rimin={Num(inputBlack)}:gimin={Num(inputBlack)}:bimin={Num(inputBlack)}:" +
                $"rimax={Num(inputWhite)}:gimax={Num(inputWhite)}:bimax={Num(inputWhite)}:" +
                $"romin={Num(outputBlack)}:gomin={Num(outputBlack)}:bomin={Num(outputBlack)}:" +
                $"romax={Num(outputWhite)}:gomax={Num(outputWhite)}:bomax={Num(outputWhite)}");
        }

        if (Math.Abs(gamma - 1) > 0.0001)
            filters.Add($"eq=gamma={Num(gamma)}");
    }

    private static void AddSplitTone(List<string> filters, VideoDedupEffects effects)
    {
        if (effects.SplitToneAmount <= 0.01 ||
            !TryColor(effects.ShadowColor, out var shadow) ||
            !TryColor(effects.HighlightColor, out var highlight))
        {
            return;
        }

        var shadowScale = effects.SplitToneAmount / 100d * (effects.SplitToneBalance >= 0 ? 1 - effects.SplitToneBalance / 100d : 1);
        var highlightScale = effects.SplitToneAmount / 100d * (effects.SplitToneBalance <= 0 ? 1 + effects.SplitToneBalance / 100d : 1);
        filters.Add(
            "colorbalance=" +
            $"rs={Num(Channel(shadow.R, shadowScale))}:gs={Num(Channel(shadow.G, shadowScale))}:bs={Num(Channel(shadow.B, shadowScale))}:" +
            $"rh={Num(Channel(highlight.R, highlightScale))}:gh={Num(Channel(highlight.G, highlightScale))}:bh={Num(Channel(highlight.B, highlightScale))}");
    }

    private static double Channel(int component, double scale) => (component / 255d - 0.5) * 2 * scale;

    private static string BuildText(VideoDedupOverlayLayer layer)
    {
        var font = ResolveFont();
        var color = layer.Color.TrimStart('#');
        return "drawtext=" +
               $"fontfile='{EscapePath(font)}':" +
               $"text='{EscapeText(layer.Text)}':" +
               $"fontsize={layer.FontSize.ToString(CultureInfo.InvariantCulture)}:" +
               $"fontcolor=0x{color}@{Num(layer.Opacity / 100d)}:" +
               $"x='w*{Num(layer.X / 100d)}':y='h*{Num(layer.Y / 100d)}'";
    }

    private static string BuildBorder(VideoDedupOverlayLayer layer)
    {
        var color = "0x" + layer.Color.TrimStart('#');
        var thickness = $"min(iw\\,ih)*{Num(layer.BorderPercent / 100d)}";
        return string.Join(",",
            $"drawbox=x=0:y=0:w=iw:h='{thickness}':color={color}:t=fill",
            $"drawbox=x=0:y='ih-({thickness})':w=iw:h='{thickness}':color={color}:t=fill",
            $"drawbox=x=0:y=0:w='{thickness}':h=ih:color={color}:t=fill",
            $"drawbox=x='iw-({thickness})':y=0:w='{thickness}':h=ih:color={color}:t=fill");
    }

    private static string ResolveFont()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in new[] { "msyh.ttc", "msyh.ttf", "simhei.ttf", "arial.ttf" })
        {
            var path = Path.Combine(fonts, name);
            if (File.Exists(path))
                return path;
        }

        throw new InvalidOperationException("画面滤镜需要系统字体来绘制文字，当前没有找到微软雅黑、黑体或 Arial。");
    }

    private static bool IsVisibleImage(VideoDedupOverlayLayer layer) =>
        layer.Kind == VideoDedupOverlayLayer.KindImage &&
        layer.Opacity > 0.01 &&
        !string.IsNullOrWhiteSpace(layer.Path);

    private static bool IsIdentity(VideoDedupEffects effects) =>
        IsIdentityCurve(effects.CurveMaster) &&
        IsIdentityCurve(effects.CurveRed) &&
        IsIdentityCurve(effects.CurveGreen) &&
        IsIdentityCurve(effects.CurveBlue);

    private static bool IsIdentityCurve(IReadOnlyList<VideoDedupCurvePoint> points) =>
        points.All(point => Math.Abs(point.Y - point.X) < 0.0001);

    private static bool TryColor(string text, out (int R, int G, int B) color)
    {
        color = default;
        if (text.Length != 7 || text[0] != '#')
            return false;
        if (!int.TryParse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !int.TryParse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !int.TryParse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        color = (red, green, blue);
        return true;
    }

    private static void Link(StringBuilder graph, string filter)
    {
        Separate(graph);
        graph.Append(filter);
    }

    private static void Separate(StringBuilder graph)
    {
        if (graph.Length > 0 && graph[^1] != ';')
            graph.Append(';');
    }

    private static string EscapePath(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        return full.Replace(":", "\\:").Replace("'", "\\'");
    }

    private static string EscapeText(string text) =>
        text.Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "\\'").Replace("%", "\\%");

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
