using Avalonia.Threading;
using PlatformPublisher.Desktop.ViewModels;
using TikTokPublisher.Core.Licensing;

namespace PlatformPublisher.Desktop.Views;

public partial class MainWindow
{
    private DispatcherTimer? _licenseVerifyTimer;
    private bool _licenseVerifyRunning;

    public void StartYunfanLicenseVerifyTimer()
    {
        _licenseVerifyTimer?.Stop();
        _licenseVerifyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(LicenseAuthService.VerifyIntervalHours),
        };
        _licenseVerifyTimer.Tick += OnYunfanLicenseVerifyTimerTick;
        _licenseVerifyTimer.Start();
    }

    public void StopYunfanLicenseVerifyTimer()
    {
        if (_licenseVerifyTimer is null)
            return;
        _licenseVerifyTimer.Tick -= OnYunfanLicenseVerifyTimerTick;
        _licenseVerifyTimer.Stop();
        _licenseVerifyTimer = null;
    }

    private async void OnYunfanLicenseVerifyTimerTick(object? sender, EventArgs e)
    {
        if (_licenseVerifyRunning)
            return;

        _licenseVerifyRunning = true;
        try
        {
            var state = await YunfanLicenseGate.VerifyAsync(forceVerify: true);
            if (state is not null)
            {
                YunfanLicenseGate.SaveVerifiedState(state);
                if (DataContext is MainWindowViewModel viewModel)
                    viewModel.StatusMessage = "软件授权联网校验通过";
                return;
            }

            StopYunfanLicenseVerifyTimer();
            if (DataContext is MainWindowViewModel failed)
                failed.StatusMessage = "软件授权联网校验失败，软件即将退出。";
            Close();
        }
        finally
        {
            _licenseVerifyRunning = false;
        }
    }
}
