using System.Globalization;

namespace TikTokPublisher.Core.Models;

public sealed class VideoDedupCurvePoint
{
    public double X { get; set; }
    public double Y { get; set; }

    public VideoDedupCurvePoint Clone() => new() { X = X, Y = Y };
}

public sealed class VideoDedupMosaicRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int Block { get; set; } = 16;

    public VideoDedupMosaicRegion Clone() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Block = Block,
    };
}

public sealed class VideoDedupOverlayLayer
{
    public const string KindImage = "image";
    public const string KindText = "text";
    public const string KindBorder = "border";

    public string Kind { get; set; } = KindImage;
    public string Path { get; set; } = "";
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#FFFFFF";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 30;
    public double Height { get; set; } = 20;
    public double Opacity { get; set; } = 100;
    public int FontSize { get; set; } = 48;
    public double BorderPercent { get; set; } = 2;

    public VideoDedupOverlayLayer Clone() => new()
    {
        Kind = Kind,
        Path = Path,
        Text = Text,
        Color = Color,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Opacity = Opacity,
        FontSize = FontSize,
        BorderPercent = BorderPercent,
    };
}

/// <summary>画面滤镜。数值为 0 表示不参与编码。亮度、对比度、饱和度、锐化和灰度仍在预设本体上。</summary>
public sealed class VideoDedupEffects
{
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Vibrance { get; set; }
    public double Exposure { get; set; }
    public double Gamma { get; set; }

    public double CurveAmount { get; set; }
    public List<VideoDedupCurvePoint> CurveMaster { get; set; } = IdentityCurve();
    public List<VideoDedupCurvePoint> CurveRed { get; set; } = IdentityCurve();
    public List<VideoDedupCurvePoint> CurveGreen { get; set; } = IdentityCurve();
    public List<VideoDedupCurvePoint> CurveBlue { get; set; } = IdentityCurve();

    public double LevelsAmount { get; set; }
    public double InputBlack { get; set; }
    public double InputWhite { get; set; } = 1;
    public double LevelsGamma { get; set; } = 1;
    public double OutputBlack { get; set; }
    public double OutputWhite { get; set; } = 1;

    public double SplitToneAmount { get; set; }
    public string ShadowColor { get; set; } = "#406080";
    public string HighlightColor { get; set; } = "#FFC080";
    public double SplitToneBalance { get; set; }

    public double LutAmount { get; set; }
    public string LutPath { get; set; } = "";

    public bool FrameMixEnabled { get; set; }
    public int FrameMixCount { get; set; } = 2;
    public double FrameMixCurrentWeight { get; set; } = 4;
    public bool ReframeEnabled { get; set; }
    public double ReframeKeepPercent { get; set; } = 95;
    public double ReframeHorizontalPercent { get; set; } = 60;
    public double ReframeVerticalPercent { get; set; } = 50;
    public bool ReframeVaryByEpisode { get; set; }
    public double NaturalColorStrength { get; set; }

    public double Blur { get; set; }
    public double VignetteAmount { get; set; }
    public double VignetteRadius { get; set; } = 70;
    public double VignetteFeather { get; set; } = 50;

    public double GrainAmount { get; set; }
    public double GrainSize { get; set; } = 15;
    public double GrainDensity { get; set; } = 50;

    public double MosaicAmount { get; set; }
    public List<VideoDedupMosaicRegion> MosaicRegions { get; set; } = [];
    public List<VideoDedupOverlayLayer> Layers { get; set; } = [];

    public static List<VideoDedupCurvePoint> IdentityCurve() =>
    [
        new() { X = 0, Y = 0 },
        new() { X = 1, Y = 1 },
    ];

    public VideoDedupEffects Clone() => new()
    {
        Temperature = Temperature,
        Tint = Tint,
        Vibrance = Vibrance,
        Exposure = Exposure,
        Gamma = Gamma,
        CurveAmount = CurveAmount,
        CurveMaster = CurveMaster.Select(point => point.Clone()).ToList(),
        CurveRed = CurveRed.Select(point => point.Clone()).ToList(),
        CurveGreen = CurveGreen.Select(point => point.Clone()).ToList(),
        CurveBlue = CurveBlue.Select(point => point.Clone()).ToList(),
        LevelsAmount = LevelsAmount,
        InputBlack = InputBlack,
        InputWhite = InputWhite,
        LevelsGamma = LevelsGamma,
        OutputBlack = OutputBlack,
        OutputWhite = OutputWhite,
        SplitToneAmount = SplitToneAmount,
        ShadowColor = ShadowColor,
        HighlightColor = HighlightColor,
        SplitToneBalance = SplitToneBalance,
        LutAmount = LutAmount,
        LutPath = LutPath,
        FrameMixEnabled = FrameMixEnabled,
        FrameMixCount = FrameMixCount,
        FrameMixCurrentWeight = FrameMixCurrentWeight,
        ReframeEnabled = ReframeEnabled,
        ReframeKeepPercent = ReframeKeepPercent,
        ReframeHorizontalPercent = ReframeHorizontalPercent,
        ReframeVerticalPercent = ReframeVerticalPercent,
        ReframeVaryByEpisode = ReframeVaryByEpisode,
        NaturalColorStrength = NaturalColorStrength,
        Blur = Blur,
        VignetteAmount = VignetteAmount,
        VignetteRadius = VignetteRadius,
        VignetteFeather = VignetteFeather,
        GrainAmount = GrainAmount,
        GrainSize = GrainSize,
        GrainDensity = GrainDensity,
        MosaicAmount = MosaicAmount,
        MosaicRegions = MosaicRegions.Select(region => region.Clone()).ToList(),
        Layers = Layers.Select(layer => layer.Clone()).ToList(),
    };

    public void Normalize()
    {
        Temperature = Math.Clamp(Temperature, -100, 100);
        Tint = Math.Clamp(Tint, -100, 100);
        Vibrance = Math.Clamp(Vibrance, -100, 100);
        Exposure = Math.Clamp(Exposure, -100, 100);
        Gamma = Math.Clamp(Gamma, -100, 100);
        CurveAmount = Math.Clamp(CurveAmount, 0, 100);
        CurveMaster = NormalizeCurve(CurveMaster);
        CurveRed = NormalizeCurve(CurveRed);
        CurveGreen = NormalizeCurve(CurveGreen);
        CurveBlue = NormalizeCurve(CurveBlue);
        LevelsAmount = Math.Clamp(LevelsAmount, 0, 100);
        InputBlack = Math.Clamp(InputBlack, 0, 1);
        InputWhite = Math.Clamp(InputWhite, 0, 1);
        if (InputWhite <= InputBlack)
            InputWhite = Math.Min(1, InputBlack + 0.01);
        LevelsGamma = Math.Clamp(LevelsGamma <= 0 ? 1 : LevelsGamma, 0.1, 3);
        OutputBlack = Math.Clamp(OutputBlack, 0, 1);
        OutputWhite = Math.Clamp(OutputWhite, 0, 1);
        if (OutputWhite <= OutputBlack)
            OutputWhite = Math.Min(1, OutputBlack + 0.01);
        SplitToneAmount = Math.Clamp(SplitToneAmount, 0, 100);
        ShadowColor = NormalizeColor(ShadowColor, "#406080");
        HighlightColor = NormalizeColor(HighlightColor, "#FFC080");
        SplitToneBalance = Math.Clamp(SplitToneBalance, -100, 100);
        LutAmount = Math.Clamp(LutAmount, 0, 100);
        LutPath = LutPath?.Trim() ?? "";
        FrameMixCount = Math.Clamp(FrameMixCount, 2, 5);
        FrameMixCurrentWeight = Math.Clamp(FrameMixCurrentWeight, 1, 10);
        ReframeKeepPercent = Math.Clamp(ReframeKeepPercent, 90, 100);
        ReframeHorizontalPercent = Math.Clamp(ReframeHorizontalPercent, 0, 100);
        ReframeVerticalPercent = Math.Clamp(ReframeVerticalPercent, 0, 100);
        NaturalColorStrength = Math.Clamp(NaturalColorStrength, 0, 100);
        Blur = Math.Clamp(Blur, 0, 20);
        VignetteAmount = Math.Clamp(VignetteAmount, 0, 100);
        VignetteRadius = Math.Clamp(VignetteRadius <= 0 ? 70 : VignetteRadius, 1, 100);
        VignetteFeather = Math.Clamp(VignetteFeather, 0, 100);
        GrainAmount = Math.Clamp(GrainAmount, 0, 100);
        GrainSize = Math.Clamp(GrainSize <= 0 ? 15 : GrainSize, 1, 40);
        GrainDensity = Math.Clamp(GrainDensity, 0, 100);
        MosaicAmount = Math.Clamp(MosaicAmount, 0, 100);
        MosaicRegions = (MosaicRegions ?? [])
            .Select(region => new VideoDedupMosaicRegion
            {
                X = Math.Clamp(region.X, 0, 100),
                Y = Math.Clamp(region.Y, 0, 100),
                Width = Math.Clamp(region.Width, 0, 100),
                Height = Math.Clamp(region.Height, 0, 100),
                Block = Math.Clamp(region.Block <= 0 ? 16 : region.Block, 2, 64),
            })
            .Where(region => region.Width > 0 && region.Height > 0)
            .ToList();
        Layers = (Layers ?? [])
            .Select(NormalizeLayer)
            .ToList();
    }

    public string FingerprintBody()
    {
        var lines = new List<string>();
        Add(lines, "temperature", Temperature);
        Add(lines, "tint", Tint);
        Add(lines, "vibrance", Vibrance);
        Add(lines, "exposure", Exposure);
        Add(lines, "gamma", Gamma);
        AddCurve(lines, "curve", CurveAmount, CurveMaster, CurveRed, CurveGreen, CurveBlue);
        AddLevels(lines);
        AddSplitTone(lines);
        if (LutAmount > 0.01 && !string.IsNullOrWhiteSpace(LutPath))
            lines.Add($"lut {Num(LutAmount)} {LutPath}");
        if (FrameMixEnabled)
            lines.Add($"frame-mix {FrameMixCount} {Num(FrameMixCurrentWeight)}");
        if (ReframeEnabled && ReframeKeepPercent < 100)
            lines.Add($"reframe {Num(ReframeKeepPercent)} {Num(ReframeHorizontalPercent)} {Num(ReframeVerticalPercent)} {ReframeVaryByEpisode}");
        Add(lines, "natural-color", NaturalColorStrength);
        Add(lines, "blur", Blur);
        if (VignetteAmount > 0.01)
            lines.Add($"vignette {Num(VignetteAmount)} {Num(VignetteRadius)} {Num(VignetteFeather)}");
        if (GrainAmount > 0.01)
            lines.Add($"grain {Num(GrainAmount)} {Num(GrainSize)} {Num(GrainDensity)}");
        if (MosaicAmount > 0.01 && MosaicRegions.Count > 0)
        {
            lines.Add("mosaic " + Num(MosaicAmount));
            foreach (var region in MosaicRegions)
                lines.Add($"region {Num(region.X)} {Num(region.Y)} {Num(region.Width)} {Num(region.Height)} {region.Block.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (var layer in Layers)
        {
            if (layer.Kind == VideoDedupOverlayLayer.KindImage && layer.Opacity > 0.01)
                lines.Add($"image {layer.Path} {Num(layer.X)} {Num(layer.Y)} {Num(layer.Width)} {Num(layer.Height)} {Num(layer.Opacity)}");
            else if (layer.Kind == VideoDedupOverlayLayer.KindText && !string.IsNullOrWhiteSpace(layer.Text) && layer.Opacity > 0.01)
                lines.Add($"text {layer.Text} {layer.Color} {layer.FontSize.ToString(CultureInfo.InvariantCulture)} {Num(layer.X)} {Num(layer.Y)} {Num(layer.Opacity)}");
            else if (layer.Kind == VideoDedupOverlayLayer.KindBorder && layer.BorderPercent > 0.01)
                lines.Add($"border {layer.Color} {Num(layer.BorderPercent)}");
        }

        return string.Join('\n', lines);
    }

    private void AddLevels(List<string> lines)
    {
        if (LevelsAmount <= 0.01)
            return;
        lines.Add($"levels {Num(LevelsAmount)} {Num(InputBlack)} {Num(InputWhite)} {Num(LevelsGamma)} {Num(OutputBlack)} {Num(OutputWhite)}");
    }

    private void AddSplitTone(List<string> lines)
    {
        if (SplitToneAmount <= 0.01)
            return;
        lines.Add($"split {Num(SplitToneAmount)} {ShadowColor} {HighlightColor} {Num(SplitToneBalance)}");
    }

    private static void AddCurve(
        List<string> lines,
        string name,
        double amount,
        params List<VideoDedupCurvePoint>[] curves)
    {
        if (amount <= 0.01 || curves.All(IsIdentity))
            return;
        lines.Add(name + " " + Num(amount));
        foreach (var curve in curves)
            lines.Add(string.Join(" ", curve.Select(point => $"{Num(point.X)}/{Num(point.Y)}")));
    }

    private static bool IsIdentity(IReadOnlyList<VideoDedupCurvePoint> points) =>
        points.All(point => Math.Abs(point.Y - point.X) < 0.0001);

    private static void Add(List<string> lines, string name, double value)
    {
        if (Math.Abs(value) > 0.01)
            lines.Add($"{name} {Num(value)}");
    }

    private static List<VideoDedupCurvePoint> NormalizeCurve(List<VideoDedupCurvePoint>? points)
    {
        var kept = new List<VideoDedupCurvePoint>();
        foreach (var point in (points ?? []).OrderBy(point => point.X))
        {
            var next = new VideoDedupCurvePoint
            {
                X = Math.Clamp(point.X, 0, 1),
                Y = Math.Clamp(point.Y, 0, 1),
            };
            if (kept.Count == 0 || next.X > kept[^1].X + 0.0001)
                kept.Add(next);
        }

        if (kept.Count == 0 || kept[0].X > 0.0001)
            kept.Insert(0, new VideoDedupCurvePoint { X = 0, Y = 0 });
        if (kept[^1].X < 0.999)
            kept.Add(new VideoDedupCurvePoint { X = 1, Y = 1 });
        return kept;
    }

    private static VideoDedupOverlayLayer NormalizeLayer(VideoDedupOverlayLayer layer)
    {
        var kind = layer.Kind switch
        {
            VideoDedupOverlayLayer.KindText or VideoDedupOverlayLayer.KindBorder => layer.Kind,
            _ => VideoDedupOverlayLayer.KindImage,
        };
        return new VideoDedupOverlayLayer
        {
            Kind = kind,
            Path = layer.Path?.Trim() ?? "",
            Text = layer.Text?.Trim() ?? "",
            Color = NormalizeColor(layer.Color, "#FFFFFF"),
            X = Math.Clamp(layer.X, 0, 100),
            Y = Math.Clamp(layer.Y, 0, 100),
            Width = Math.Clamp(layer.Width, 0, 100),
            Height = Math.Clamp(layer.Height, 0, 100),
            Opacity = Math.Clamp(layer.Opacity, 0, 100),
            FontSize = Math.Clamp(layer.FontSize <= 0 ? 48 : layer.FontSize, 8, 200),
            BorderPercent = Math.Clamp(layer.BorderPercent, 0, 20),
        };
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 7 && text[0] == '#' && text[1..].All(Uri.IsHexDigit))
            return text.ToUpperInvariant();
        return fallback;
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
