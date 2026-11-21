using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ChannelsPublisher.Clip;
using CommunityToolkit.Mvvm.ComponentModel;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;

namespace TikTokPublisher.Ui.Views;

internal sealed partial class MaterialClipOutput(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    private string _summary = "读取时长和来源集数…";

    public Task<ClipOutputReview>? Review { get; private set; }

    public async Task LoadReviewAsync(string ffprobe)
    {
        Review = ReadReviewAsync(ffprobe);
        var review = await Review;
        var episodes = review.Segments.Select(segment => segment.Episode).Distinct().ToArray();
        var duration = review.DurationSeconds is { } seconds ? $"{seconds:0.0} 秒" : "时长未知";
        Summary = episodes.Length == 0 ? duration : $"{duration} · {string.Join(" → ", episodes.Select(episode => $"第 {episode} 集"))}";
    }

    private async Task<ClipOutputReview> ReadReviewAsync(string ffprobe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        ClipOutputReview? review = null;
        try
        {
            review = await ClipEngine.ReadOutputReviewAsync(Path, timeout.Token);
            if (review?.DurationSeconds is not null)
                return review;
            var duration = await Ffmpeg.ProbeDurationSecondsAsync(ffprobe, Path, timeout.Token);
            return new ClipOutputReview(duration, review?.Segments ?? []);
        }
        catch
        {
            return review ?? new ClipOutputReview(null, []);
        }
    }
}

internal sealed class MaterialClipPreviewWindow : Window
{
    private readonly MaterialClipOutput _output;
    private readonly WebView2Host? _player;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private double _startSeconds;
    private double? _stopSeconds;
    private bool _closed;
    private bool _hasMedia;
    private int _seekVersion;

    public MaterialClipPreviewWindow(MaterialClipOutput output, Action<string> openWithSystem)
    {
        _output = output;
        Title = output.Name;
        Width = 900;
        Height = 660;
        MinWidth = 580;
        MinHeight = 410;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        Content = layout;

        var toolbar = new WrapPanel { ItemSpacing = 10, LineSpacing = 6 };
        var external = new Button { Content = "用系统播放器打开" };
        external.Click += (_, _) => openWithSystem(output.Path);
        toolbar.Children.Add(external);
        toolbar.Children.Add(_status);
        layout.Children.Add(toolbar);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 10 };
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        var moments = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 8, 0) };
        var momentScroll = new ScrollViewer { Content = moments, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(momentScroll, 1);
        body.Children.Add(momentScroll);

        if (OperatingSystem.IsWindows())
        {
            _player = new WebView2Host { UserDataFolder = Path.Combine(AppPaths.DataRoot, "material-clip-preview") };
            _player.SetRenderedVisible(true);
            _player.RuntimeMissing += () => ShowPlayerError("缺少 WebView2，可用系统播放器打开。");
            _player.ProcessFailed += _ => ShowPlayerError("预览播放器无法继续，可用系统播放器打开。");
            _player.NavigationCompleted += async _ => await OnNavigatedAsync();
            body.Children.Add(_player);
            _status.Text = "正在加载本地成片…";
            Opened += (_, _) => _player.Navigate(new Uri(Path.GetFullPath(_output.Path)).AbsoluteUri);
        }
        else
            ShowPlayerError("请用系统播放器打开成片。");

        Opened += async (_, _) =>
        {
            await PopulateMomentsAsync(moments);
            if (_player is null)
                return;
            await Task.Delay(TimeSpan.FromSeconds(8));
            if (!_closed && !_hasMedia)
                ShowPlayerError("预览未能加载，可用系统播放器打开。");
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _player?.CloseBrowser();
        };
    }

    private void ShowPlayerError(string message)
    {
        _status.Text = message;
        if (_player is not null)
            _player.SetRenderedVisible(false);
    }

    private async Task OnNavigatedAsync()
    {
        if (_closed || _player is null)
            return;
        _hasMedia = await _player.ExecuteScriptAsync("Boolean(document.querySelector('video'))") == "true";
        if (_closed)
            return;
        if (!_hasMedia)
        {
            ShowPlayerError("此视频无法在内置播放器打开，可用系统播放器打开。");
            return;
        }
        _player.SetRenderedVisible(true);
        _status.Text = "";
        if (_stopSeconds is not null)
            await SeekAsync();
    }

    private async Task SeekAsync()
    {
        if (!_hasMedia || _player is null || _stopSeconds is not { } stop)
            return;
        var startText = _startSeconds.ToString(CultureInfo.InvariantCulture);
        var stopText = stop.ToString(CultureInfo.InvariantCulture);
        var version = ++_seekVersion;
        var script = $$"""
            (() => {
                const video = document.querySelector('video');
                if (!video) return false;
                window.clipReviewSeek = {{version}};
                const play = () => {
                    if (window.clipReviewSeek !== {{version}} || video.error) return;
                    video.currentTime = {{startText}};
                    video.addEventListener('timeupdate', function untilEnd() {
                        const current = window.clipReviewSeek === {{version}};
                        if (!current || video.currentTime >= {{stopText}}) {
                            if (current) video.pause();
                            video.removeEventListener('timeupdate', untilEnd);
                        }
                    });
                    video.play().catch(() => {});
                };
                if (video.readyState > 0) play();
                else video.addEventListener('loadedmetadata', play, { once: true });
                return !video.error;
            })()
            """;
        if (await _player.ExecuteScriptAsync(script) != "true" && !_closed)
            ShowPlayerError("无法跳到切点，可用系统播放器打开。");
    }

    private async Task PopulateMomentsAsync(Panel panel)
    {
        var review = await (_output.Review ?? Task.FromResult(new ClipOutputReview(null, [])));
        if (_closed)
            return;
        panel.Children.Add(new TextBlock { Text = "来源与切点", FontWeight = FontWeight.SemiBold });
        var timeline = BuildMoments(review.Segments);
        if (timeline.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "此成片没有来源时间轴。", Classes = { "pageSummary" }, TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var moment in timeline)
        {
            var time = TimeSpan.FromSeconds(moment.AtSeconds).ToString(@"mm\:ss\.f");
            var button = new Button
            {
                Content = new TextBlock { Text = $"{time}  {moment.Label}", TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = _player is not null,
            };
            button.Click += async (_, _) =>
            {
                _startSeconds = Math.Max(0, moment.AtSeconds - 3);
                _stopSeconds = moment.AtSeconds + 4;
                await SeekAsync();
            };
            panel.Children.Add(button);
        }
    }

    internal static IReadOnlyList<(double AtSeconds, string Label)> BuildMoments(IReadOnlyList<ClipReviewSegment> segments)
    {
        if (segments.Count == 0)
            return [];
        var moments = new List<(double, string)> { (0, $"开头 · 第 {segments[0].Episode} 集") };
        double elapsed = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (index > 0 && segments[index - 1].Episode != segment.Episode)
                moments.Add((elapsed, $"第 {segments[index - 1].Episode} 集 → 第 {segment.Episode} 集"));
            if (!string.IsNullOrWhiteSpace(segment.VisualDescription))
                moments.Add((elapsed, $"画面复评 · {segment.VisualDescription}"));
            elapsed += Math.Max(0, segment.EndMs - (double)segment.StartMs) / 1000;
        }
        moments.Add((Math.Max(0, elapsed - 3), $"结尾 · 第 {segments[^1].Episode} 集"));
        return moments;
    }
}
