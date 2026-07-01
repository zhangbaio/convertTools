using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChannelsPublisher.Prep;
using ConvertTools.App.Services;

namespace ConvertTools.App.Views;

public partial class TranscodeView : UserControl
{
    private static readonly string[] VideoExts = { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".flv", ".wmv", ".webm" };
    private bool _running;

    public TranscodeView() => InitializeComponent();

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        if (Storage is null) return;
        var dirs = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择包含视频的文件夹", AllowMultiple = false });
        var dir = dirs.FirstOrDefault();
        if (dir != null) FolderBox.Text = dir.Path.LocalPath;
    }

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        var folder = FolderBox.Text?.Trim();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { Status.Text = "请先选择有效文件夹"; return; }

        var videos = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (videos.Count == 0) { Status.Text = "该文件夹没有视频文件"; return; }

        var outDir = Path.Combine(folder, "_transcoded");
        Directory.CreateDirectory(outDir);
        var ffmpeg = AppConfig.Current.FfmpegPath;

        _running = true;
        StartBtn.IsEnabled = false;
        LogList.Items.Clear();
        int ok = 0, fail = 0;
        for (int i = 0; i < videos.Count; i++)
        {
            var v = videos[i];
            var name = Path.GetFileName(v);
            Status.Text = $"转码中 {i + 1}/{videos.Count}：{name}";
            var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(v) + ".mp4");
            try
            {
                await FfmpegRunner.RunAsync(ffmpeg, new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error", "-i", v,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", outPath,
                }, CancellationToken.None);
                ok++;
                Log($"✓ {name}");
            }
            catch (Exception ex)
            {
                fail++;
                Log($"✗ {name} —— {ex.Message}");
            }
        }
        Status.Text = $"完成：成功 {ok}，失败 {fail}，输出目录 {outDir}";
        StartBtn.IsEnabled = true;
        _running = false;
    }

    private void Log(string line) => Dispatcher.UIThread.Post(() => LogList.Items.Add(line));
}
