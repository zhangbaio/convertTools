using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Weixin.Publishing;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Desktop.Views;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.DependencyInjection;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure.Rendering;
using TikTokPublisher.Core.Drama;
using ClientSettingsStore = TikTokPublisher.Core.Services.ClientSettingsStore;
using TikTokPublisher.Ui.ViewModels;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Security;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Analytics.Services;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Kuaishou.Analytics;
using PlatformPublisher.Weixin.Analytics;
using PlatformPublisher.Persistence;
using PlatformPublisher.Materials;
using PlatformPublisher.Publishing.Execution;
using PlatformPublisher.Publishing.Storage;

namespace PlatformPublisher.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            YunfanLicenseGate.Configure();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (!await YunfanLicenseGate.EnsureBeforeMainWindowAsync())
                    {
                        desktop.Shutdown();
                        return;
                    }

                    StartMainWindow(desktop);
                }
                catch (Exception ex)
                {
                    var window = new Window
                    {
                        Title = "云帆启动失败",
                        Width = 720,
                        Height = 420,
                        Content = new TextBlock
                        {
                            Text = ex.ToString(),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(16),
                        },
                    };
                    desktop.MainWindow = window;
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    window.Show();
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
            var workspaceLease = KuaishouWorkspaceLease.Acquire();
            desktop.Exit += (_, _) => workspaceLease.Dispose();
            ShortDramaDramaServices.UseSettingsDatabase(PlatformPublisherPaths.SettingsDatabasePath);
            _services = PlatformPublisherServices.Build();
            _ = _services.GetRequiredService<AnalyticsRepository>();
            _services.GetRequiredService<LegacyDatabaseImporter>().Import(
                PlatformPublisherPaths.LegacySettingsDatabasePath,
                PlatformPublisherPaths.LegacyAnalyticsDatabasePath);
            KuaishouPersonalConfig.ConfigureDatabase(
                _services.GetRequiredService<AccountJsonSettingStore>(),
                _services.GetRequiredService<KuaishouCredentialStore>());
            var importedLegacyAccounts = Task.Run(() =>
                    _services.GetRequiredService<LegacyAccountSessionImportService>().ImportAllLegacyProfilesAsync())
                .GetAwaiter().GetResult();
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            viewModel.Initialization.GetAwaiter().GetResult();
            if (importedLegacyAccounts > 0)
                viewModel.StatusMessage = $"已迁移 {importedLegacyAccounts} 个短剧助手账号及其配置和登录状态。";
            var kuaishouOnlineWorker = _services.GetRequiredService<KuaishouOnlineQueueWorker>();
            kuaishouOnlineWorker.Start();
            var migratedDrafts=_services.GetRequiredService<LegacyPublishDraftMigrator>().MigrateAsync().GetAwaiter().GetResult();
            var settingsViewModel = _services.GetRequiredService<SystemSettingsViewModel>();
            var publishCoordinator = _services.GetRequiredService<PlatformPublishCoordinator>();
            var mainWindow = new MainWindow { DataContext = viewModel };
            mainWindow.BindDatabaseMaintenance(_services.GetRequiredService<PlatformDatabase>(),_services.GetRequiredService<DatabaseBackupService>());
            mainWindow.BindWindowPlacement(_services.GetRequiredService<IJsonSettingStore>());
            mainWindow.BindAccountDatabase(_services.GetRequiredService<ChannelsPublisher.Core.Services.AccountStore>());
            mainWindow.BindLegacySessionImport(_services.GetRequiredService<LegacyAccountSessionImportService>());
            mainWindow.BindSettings(settingsViewModel);
            mainWindow.BindXinge();
            mainWindow.BindAdxSettings(_services.GetRequiredService<AdxAutomationService>());
            if (YunfanFeatures.WeixinAssistantEnabled)
            {
                mainWindow.BindWeixinSeries(publishCoordinator.GetAdapter(PublishPlatform.WeixinChannel));
                mainWindow.BindWeixinWorkflow(viewModel, _services.GetRequiredService<ShortDrama.Core.Interfaces.IProjectScanner>(),
                    _services.GetRequiredService<AdxAutomationService>(), _services.GetRequiredService<AdxBatchStore>(),
                    _services.GetRequiredService<WeixinDirectoryMaterialPublishService>(),
                    _services.GetRequiredService<WeixinMaterialDownloadService>(),
                    _services.GetRequiredService<WeixinHighlightScheduleService>(),
                    _services.GetRequiredService<ShortDrama.Desktop.Services.WeixinMaterialChannelVideoDeleteService>(),
                    _services.GetRequiredService<UnifiedPublishViewModel>());
            }
            mainWindow.BindKuaishouAdx(_services.GetRequiredService<AdxAutomationService>(),
                _services.GetRequiredService<AdxBatchStore>(), _services.GetRequiredService<KuaishouAdxBatchResolver>());
            if(migratedDrafts>0)viewModel.StatusMessage=$"已将 {migratedDrafts} 个旧素材任务迁移为统一发布草稿。";
            mainWindow.BindWeixinDownload(viewModel);
            mainWindow.BindAnalytics(_services.GetRequiredService<AnalyticsViewModel>(), viewModel);
            var highlightScheduler = _services.GetRequiredService<WeixinHighlightScheduleService>();
            if (YunfanFeatures.WeixinAssistantEnabled)
                highlightScheduler.Start();
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) =>
            {
                mainWindow.StopYunfanLicenseVerifyTimer();
                highlightScheduler.Dispose();
                kuaishouOnlineWorker.Dispose();
                viewModel.Shutdown();
            };
            mainWindow.Show();
            mainWindow.StartYunfanLicenseVerifyTimer();
    }
}
