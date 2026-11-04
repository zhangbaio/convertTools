using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TikTokPublisher.Core.Remote;

namespace TikTokPublisher.Ui.Views;

public static class XingeDispatchConfirmPrompt
{
    public static void Show(DispatchConfirmSession session)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (session.Decision.Task.IsCompleted)
                return;
            var window = new XingeDispatchConfirmWindow(session);
            window.Show();
        });
    }
}

public sealed class XingeDispatchConfirmWindow : Window
{
    private readonly DispatchConfirmSession _session;
    private readonly TextBlock _body;
    private readonly TextBlock _countdown;
    private readonly DispatcherTimer _timer;
    private bool _decided;

    public XingeDispatchConfirmWindow(DispatchConfirmSession session)
    {
        _session = session;
        Title = "XINGE 派单";
        Topmost = true;
        CanResize = false;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Padding = new Avalonia.Thickness(20);

        _body = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _countdown = new TextBlock { FontSize = 16, Margin = new Avalonia.Thickness(0, 12, 0, 12) };
        var rejectOnce = new Button { Content = "拒绝本次", MinWidth = 110 };
        var rejectToday = new Button { Content = "今日不再接收", MinWidth = 130 };
        var execute = new Button { Content = "立即执行", MinWidth = 110 };
        rejectOnce.Click += (_, _) => Decide(DispatchConfirmDecision.RejectOnce);
        rejectToday.Click += (_, _) => Decide(DispatchConfirmDecision.RejectToday);
        execute.Click += (_, _) => Decide(DispatchConfirmDecision.Execute);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(rejectOnce);
        buttons.Children.Add(rejectToday);
        buttons.Children.Add(execute);

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
                Decide(DispatchConfirmDecision.Execute);
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Closing += (_, _) =>
        {
            if (_decided)
                return;
            _decided = true;
            _session.Decision.TrySetResult(DispatchConfirmDecision.RejectOnce);
        };
        Refresh();
    }

    private void Refresh()
    {
        _body.Text = _session.BodyText();
        var left = _session.RemainingSeconds(DateTimeOffset.Now);
        _countdown.Text = left <= 0 ? "即将自动执行" : $"{left} 秒后自动执行";
    }

    private void Decide(DispatchConfirmDecision decision)
    {
        if (_decided)
            return;
        _decided = true;
        _session.Decision.TrySetResult(decision);
        Close();
    }
}
