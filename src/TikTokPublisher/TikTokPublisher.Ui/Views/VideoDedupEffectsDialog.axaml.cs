using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.Views;

public partial class VideoDedupEffectsDialog : Window
{
    private readonly List<MosaicRow> _mosaicRows = [];
    private readonly List<LayerRow> _layerRows = [];
    private bool _applied;

    public VideoDedupEffectsDialog()
    {
        InitializeComponent();
    }

    public static async Task<bool> EditAsync(Window owner, VideoDedupPreset target)
    {
        var dialog = new VideoDedupEffectsDialog();
        dialog.Load(target);
        await dialog.ShowDialog(owner);
        return dialog._applied && dialog.TryWrite(target, out _);
    }

    private void Load(VideoDedupPreset preset)
    {
        var effects = preset.Effects ?? new VideoDedupEffects();
        BrightnessBox.Value = (decimal)preset.Brightness;
        ContrastBox.Value = (decimal)preset.Contrast;
        SaturationBox.Value = (decimal)preset.Saturation;
        SharpnessBox.Value = (decimal)preset.Sharpness;
        GrayscaleBox.IsChecked = preset.Grayscale;
        TemperatureBox.Value = (decimal)effects.Temperature;
        TintBox.Value = (decimal)effects.Tint;
        VibranceBox.Value = (decimal)effects.Vibrance;
        ExposureBox.Value = (decimal)effects.Exposure;
        GammaBox.Value = (decimal)effects.Gamma;
        CurveAmountBox.Value = (decimal)effects.CurveAmount;
        MasterCurveBox.Text = FormatCurve(effects.CurveMaster);
        RedCurveBox.Text = FormatCurve(effects.CurveRed);
        GreenCurveBox.Text = FormatCurve(effects.CurveGreen);
        BlueCurveBox.Text = FormatCurve(effects.CurveBlue);
        LevelsAmountBox.Value = (decimal)effects.LevelsAmount;
        InputBlackBox.Value = (decimal)effects.InputBlack;
        InputWhiteBox.Value = (decimal)effects.InputWhite;
        LevelsGammaBox.Value = (decimal)effects.LevelsGamma;
        OutputBlackBox.Value = (decimal)effects.OutputBlack;
        OutputWhiteBox.Value = (decimal)effects.OutputWhite;
        SplitAmountBox.Value = (decimal)effects.SplitToneAmount;
        ShadowColorBox.Text = effects.ShadowColor;
        HighlightColorBox.Text = effects.HighlightColor;
        SplitBalanceBox.Value = (decimal)effects.SplitToneBalance;
        LutAmountBox.Value = (decimal)effects.LutAmount;
        LutPathBox.Text = effects.LutPath;
        BlurBox.Value = (decimal)effects.Blur;
        VignetteAmountBox.Value = (decimal)effects.VignetteAmount;
        VignetteRadiusBox.Value = (decimal)effects.VignetteRadius;
        VignetteFeatherBox.Value = (decimal)effects.VignetteFeather;
        GrainAmountBox.Value = (decimal)effects.GrainAmount;
        GrainSizeBox.Value = (decimal)effects.GrainSize;
        GrainDensityBox.Value = (decimal)effects.GrainDensity;
        MosaicAmountBox.Value = (decimal)effects.MosaicAmount;
        foreach (var region in effects.MosaicRegions)
            AddMosaicRow(region);
        foreach (var layer in effects.Layers)
            AddLayerRow(layer);
    }

    private bool TryWrite(VideoDedupPreset target, out string error)
    {
        try
        {
            target.Brightness = Number(BrightnessBox);
            target.Contrast = Number(ContrastBox, 1);
            target.Saturation = Number(SaturationBox, 1);
            target.Sharpness = Number(SharpnessBox);
            target.Grayscale = GrayscaleBox.IsChecked == true;
            target.Effects ??= new VideoDedupEffects();
            var effects = target.Effects;
            effects.Temperature = Number(TemperatureBox);
            effects.Tint = Number(TintBox);
            effects.Vibrance = Number(VibranceBox);
            effects.Exposure = Number(ExposureBox);
            effects.Gamma = Number(GammaBox);
            effects.CurveAmount = Number(CurveAmountBox);
            effects.CurveMaster = ParseCurve(MasterCurveBox.Text, "主通道");
            effects.CurveRed = ParseCurve(RedCurveBox.Text, "红通道");
            effects.CurveGreen = ParseCurve(GreenCurveBox.Text, "绿通道");
            effects.CurveBlue = ParseCurve(BlueCurveBox.Text, "蓝通道");
            effects.LevelsAmount = Number(LevelsAmountBox);
            effects.InputBlack = Number(InputBlackBox);
            effects.InputWhite = Number(InputWhiteBox, 1);
            effects.LevelsGamma = Number(LevelsGammaBox, 1);
            effects.OutputBlack = Number(OutputBlackBox);
            effects.OutputWhite = Number(OutputWhiteBox, 1);
            effects.SplitToneAmount = Number(SplitAmountBox);
            effects.ShadowColor = ShadowColorBox.Text ?? "";
            effects.HighlightColor = HighlightColorBox.Text ?? "";
            effects.SplitToneBalance = Number(SplitBalanceBox);
            effects.LutAmount = Number(LutAmountBox);
            effects.LutPath = LutPathBox.Text?.Trim() ?? "";
            effects.Blur = Number(BlurBox);
            effects.VignetteAmount = Number(VignetteAmountBox);
            effects.VignetteRadius = Number(VignetteRadiusBox, 70);
            effects.VignetteFeather = Number(VignetteFeatherBox, 50);
            effects.GrainAmount = Number(GrainAmountBox);
            effects.GrainSize = Number(GrainSizeBox, 15);
            effects.GrainDensity = Number(GrainDensityBox, 50);
            effects.MosaicAmount = Number(MosaicAmountBox);
            effects.MosaicRegions = _mosaicRows.Select(row => row.Read()).ToList();
            effects.Layers = _layerRows.Select(row => row.Read()).ToList();
            target.Normalize();
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private async void OnPickPreviewClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择预览视频", ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.m4v"]);
        if (!string.IsNullOrWhiteSpace(path))
            PreviewPathBox.Text = path;
    }

    private async void OnPickLutClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择 3D LUT", ["*.cube", "*.3dl"]);
        if (!string.IsNullOrWhiteSpace(path))
            LutPathBox.Text = path;
    }

    private async void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        var video = PreviewPathBox.Text?.Trim() ?? "";
        if (video.Length == 0 || !File.Exists(video))
        {
            PreviewStatusText.Text = "请先选择一个存在的预览视频。";
            return;
        }

        var draft = new VideoDedupPreset();
        if (!TryWrite(draft, out var error))
        {
            PreviewStatusText.Text = error;
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "tiktok-dedup-preview");
        Directory.CreateDirectory(directory);
        var stamp = Guid.NewGuid().ToString("N");
        var original = Path.Combine(directory, stamp + "-original.png");
        var filtered = Path.Combine(directory, stamp + "-filtered.png");
        try
        {
            PreviewStatusText.Text = "正在生成预览…";
            await VideoDedupEffectFilters.RenderPreviewAsync(
                draft,
                video,
                Number(PreviewTimeBox),
                original,
                filtered,
                CancellationToken.None);
            OriginalView.Source = LoadBitmap(original);
            FilteredView.Source = LoadBitmap(filtered);
            PreviewStatusText.Text = VideoDedupEffectFilters.Describe(draft).Count == 0
                ? "当前滤镜都未启用，两张画面一致。"
                : "预览已更新。应用后仍需保存账号配置。";
        }
        catch (Exception ex)
        {
            PreviewStatusText.Text = ex.Message;
        }
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        var draft = new VideoDedupPreset();
        if (!TryWrite(draft, out var error))
        {
            PreviewStatusText.Text = error;
            return;
        }

        _applied = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnResetAllClick(object? sender, RoutedEventArgs e)
    {
        Load(VideoDedupPreset.CreateDefault());
        MosaicRows.Children.Clear();
        LayerRows.Children.Clear();
        _mosaicRows.Clear();
        _layerRows.Clear();
        PreviewStatusText.Text = "已恢复默认，所有滤镜关闭。";
    }

    private void OnAddMosaicClick(object? sender, RoutedEventArgs e) =>
        AddMosaicRow(new VideoDedupMosaicRegion { X = 10, Y = 10, Width = 30, Height = 20, Block = 16 });

    private void OnAddImageClick(object? sender, RoutedEventArgs e) =>
        AddLayerRow(new VideoDedupOverlayLayer { Kind = VideoDedupOverlayLayer.KindImage });

    private void OnAddTextClick(object? sender, RoutedEventArgs e) =>
        AddLayerRow(new VideoDedupOverlayLayer { Kind = VideoDedupOverlayLayer.KindText, Text = "文字", Color = "#FFFFFF" });

    private void OnAddBorderClick(object? sender, RoutedEventArgs e) =>
        AddLayerRow(new VideoDedupOverlayLayer { Kind = VideoDedupOverlayLayer.KindBorder, Color = "#FFFFFF", BorderPercent = 2 });

    private void AddMosaicRow(VideoDedupMosaicRegion region)
    {
        var row = new MosaicRow(region);
        row.Remove.Click += (_, _) =>
        {
            _mosaicRows.Remove(row);
            MosaicRows.Children.Remove(row.View);
        };
        _mosaicRows.Add(row);
        MosaicRows.Children.Add(row.View);
    }

    private void AddLayerRow(VideoDedupOverlayLayer layer)
    {
        var row = new LayerRow(layer, PickFileAsync);
        row.Remove.Click += (_, _) =>
        {
            _layerRows.Remove(row);
            LayerRows.Children.Remove(row.View);
        };
        _layerRows.Add(row);
        LayerRows.Children.Add(row.View);
    }

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns.ToList() }],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private static Bitmap LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        return new Bitmap(stream);
    }

    private static double Number(NumericUpDown box, double fallback = 0) =>
        (double)(box.Value ?? (decimal)fallback);

    private static string FormatCurve(IReadOnlyList<VideoDedupCurvePoint>? points)
    {
        var source = points is { Count: > 0 } ? points : VideoDedupEffects.IdentityCurve();
        return string.Join(" ", source.Select(point =>
            $"{point.X.ToString("0.####", CultureInfo.InvariantCulture)},{point.Y.ToString("0.####", CultureInfo.InvariantCulture)}"));
    }

    private static List<VideoDedupCurvePoint> ParseCurve(string? text, string name)
    {
        var parts = (text ?? "").Split([' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return VideoDedupEffects.IdentityCurve();
        var points = new List<VideoDedupCurvePoint>();
        foreach (var part in parts)
        {
            var pair = part.Split([',', '/']);
            if (pair.Length != 2 ||
                !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                throw new InvalidOperationException($"{name}曲线的格式应为 0,0 1,1。");
            }

            points.Add(new VideoDedupCurvePoint { X = x, Y = y });
        }

        return points;
    }

    private sealed class MosaicRow
    {
        private readonly NumericUpDown _x;
        private readonly NumericUpDown _y;
        private readonly NumericUpDown _width;
        private readonly NumericUpDown _height;
        private readonly NumericUpDown _block;

        public MosaicRow(VideoDedupMosaicRegion region)
        {
            _x = Box(region.X, 0, 100);
            _y = Box(region.Y, 0, 100);
            _width = Box(region.Width, 1, 100);
            _height = Box(region.Height, 1, 100);
            _block = Box(region.Block, 2, 64);
            Remove = new Button { Content = "删除" };
            View = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    Label("X"), _x, Label("Y"), _y, Label("宽"), _width, Label("高"), _height, Label("块"), _block, Remove,
                },
            };
        }

        public Button Remove { get; }
        public StackPanel View { get; }

        public VideoDedupMosaicRegion Read() => new()
        {
            X = (double)(_x.Value ?? 0),
            Y = (double)(_y.Value ?? 0),
            Width = (double)(_width.Value ?? 0),
            Height = (double)(_height.Value ?? 0),
            Block = (int)(_block.Value ?? 16),
        };
    }

    private sealed class LayerRow
    {
        private readonly ComboBox _kind;
        private readonly TextBox _content;
        private readonly TextBox _color;
        private readonly NumericUpDown _x;
        private readonly NumericUpDown _y;
        private readonly NumericUpDown _width;
        private readonly NumericUpDown _height;
        private readonly NumericUpDown _opacity;
        private readonly NumericUpDown _fontSize;
        private readonly NumericUpDown _border;
        private readonly Button _browse;

        public LayerRow(VideoDedupOverlayLayer layer, Func<string, IReadOnlyList<string>, Task<string?>> pickFile)
        {
            _kind = new ComboBox { Width = 80 };
            _kind.Items.Add("图片");
            _kind.Items.Add("文字");
            _kind.Items.Add("边框");
            _kind.SelectedIndex = layer.Kind switch
            {
                VideoDedupOverlayLayer.KindText => 1,
                VideoDedupOverlayLayer.KindBorder => 2,
                _ => 0,
            };
            _content = new TextBox
            {
                Width = 180,
                Text = layer.Kind == VideoDedupOverlayLayer.KindText ? layer.Text : layer.Path,
                Watermark = layer.Kind == VideoDedupOverlayLayer.KindText ? "文字" : "图片路径",
            };
            _color = new TextBox { Width = 90, Text = layer.Color, Watermark = "#FFFFFF" };
            _browse = new Button { Content = "选择" };
            _browse.Click += async (_, _) =>
            {
                if (_kind.SelectedIndex != 0)
                    return;
                var path = await pickFile("选择图片", ["*.png", "*.jpg", "*.jpeg", "*.webp"]);
                if (!string.IsNullOrWhiteSpace(path))
                    _content.Text = path;
            };
            _x = Box(layer.X, 0, 100);
            _y = Box(layer.Y, 0, 100);
            _width = Box(layer.Width <= 0 ? 30 : layer.Width, 0, 100);
            _height = Box(layer.Height <= 0 ? 20 : layer.Height, 0, 100);
            _opacity = Box(layer.Opacity, 0, 100);
            _fontSize = Box(layer.FontSize, 8, 200);
            _border = Box(layer.BorderPercent, 0, 20);
            Remove = new Button { Content = "删除" };
            View = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 6,
                        Children = { _kind, _content, _color, _browse, Remove },
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            Label("X"), _x, Label("Y"), _y, Label("宽"), _width, Label("高"), _height,
                            Label("透明"), _opacity, Label("字号"), _fontSize, Label("边框%"), _border,
                        },
                    },
                },
            };
        }

        public Button Remove { get; }
        public StackPanel View { get; }

        public VideoDedupOverlayLayer Read()
        {
            var text = _content.Text?.Trim() ?? "";
            var kind = _kind.SelectedIndex switch
            {
                1 => VideoDedupOverlayLayer.KindText,
                2 => VideoDedupOverlayLayer.KindBorder,
                _ => VideoDedupOverlayLayer.KindImage,
            };
            return new VideoDedupOverlayLayer
            {
                Kind = kind,
                Path = kind == VideoDedupOverlayLayer.KindImage ? text : "",
                Text = kind == VideoDedupOverlayLayer.KindText ? text : "",
                Color = string.IsNullOrWhiteSpace(_color.Text) ? "#FFFFFF" : _color.Text.Trim(),
                X = (double)(_x.Value ?? 0),
                Y = (double)(_y.Value ?? 0),
                Width = (double)(_width.Value ?? 30),
                Height = (double)(_height.Value ?? 20),
                Opacity = (double)(_opacity.Value ?? 100),
                FontSize = (int)(_fontSize.Value ?? 48),
                BorderPercent = (double)(_border.Value ?? 2),
            };
        }
    }

    private static NumericUpDown Box(double value, double minimum, double maximum) => new()
    {
        Width = 72,
        Minimum = (decimal)minimum,
        Maximum = (decimal)maximum,
        Value = (decimal)value,
        Increment = 1,
        FormatString = "0",
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
}
