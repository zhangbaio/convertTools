using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TikTokPublisher.Core.Remote;

namespace TikTokPublisher.Ui.Views;

public static class XingeRestartResumePrompt
{
    public static Task<bool> ConfirmAsync(DispatchConfirmSession session)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Open()
        {
            if (done.Task.IsCompleted)
                return;
            var window = new XingeRestartResumeWindow(session, done);
            window.Show();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Open();
        else
            Dispatcher.UIThread.Post(Open);
        return done.Task;
    }
}

public sealed class XingeRestartResumeWindow : Window
{
    private readonly DispatchConfirmSession _session;
    private readonly TaskCompletionSource<bool> _done;
    private readonly TextBlock _body;
    private readonly TextBlock _countdown;
    private readonly DispatcherTimer _timer;
    private bool _decided;

    public XingeRestartResumeWindow(DispatchConfirmSession session, TaskCompletionSource<bool> done)
    {
        _session = session;
        _done = done;
        Title = "继续执行已下发剧集";
        Topmost = true;
        CanResize = false;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Padding = new Avalonia.Thickness(20);

        _body = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _countdown = new TextBlock { FontSize = 16, Margin = new Avalonia.Thickness(0, 12, 0, 12) };
        var execute = new Button { Content = "立即执行", MinWidth = 110 };
        var dismiss = new Button { Content = "暂不执行", MinWidth = 110 };
        execute.Click += (_, _) => Finish(true);
        dismiss.Click += (_, _) => Finish(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { dismiss, execute },
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new ScrollViewer { MaxHeight = 360, Content = _body },
                _countdown,
                buttons,
            },
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            Refresh();
            if (!_decided && _session.IsExpired(DateTimeOffset.Now))
                Finish(true);
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Closing += (_, _) =>
        {
            if (_decided)
                return;
            _decided = true;
            _done.TrySetResult(false);
        };
        Refresh();
    }

    private void Refresh()
    {
        _body.Text = "客户端已重启，即将继续执行 XINGE 已下发的剧集。"
            + Environment.NewLine
            + Environment.NewLine
            + _session.BodyText();
        var left = _session.RemainingSeconds(DateTimeOffset.Now);
        _countdown.Text = left <= 0 ? "即将自动执行" : $"{left} 秒后自动执行";
    }

    private void Finish(bool execute)
    {
        if (_decided)
            return;
        _decided = true;
        _done.TrySetResult(execute);
        Close();
    }
}
