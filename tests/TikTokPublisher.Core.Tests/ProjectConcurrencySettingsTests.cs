using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Core.Tests;

public sealed class ProjectConcurrencySettingsTests
{
    [Fact]
    public void Unified_settings_keep_project_and_upload_limits_isolated_by_platform()
    {
        var temporary = Directory.CreateTempSubdirectory("project-concurrency-");
        try
        {
            var tikTok = Path.Combine(temporary.FullName, "tiktok.db");
            var kuaishou = Path.Combine(temporary.FullName, "kuaishou.db");
            ClientSettingsStore.Save(new ClientSettings
            {
                TiktokGlobalProjectConcurrency = 3,
                TiktokUploadConcurrency = 5,
            }, tikTok);
            ClientSettingsStore.Save(new ClientSettings
            {
                KuaishouGlobalProjectConcurrency = 7,
                TiktokUploadConcurrency = 6,
            }, kuaishou);
            var viewModel = new SystemSettingsViewModel(
                tikTok, kuaishouConcurrencyDatabasePath: kuaishou).ProjectConcurrencySettings;
            viewModel.Load();
            Assert.Equal(3m, viewModel.TikTokProjectConcurrency);
            Assert.Equal(7m, viewModel.KuaishouProjectConcurrency);

            viewModel.TikTokProjectConcurrency = 2.5m;
            viewModel.KuaishouProjectConcurrency = 8;
            viewModel.SaveCommand.Execute(null);
            Assert.Equal(3, ClientSettingsStore.Load(tikTok).TiktokGlobalProjectConcurrency);
            Assert.Equal(7, ClientSettingsStore.Load(kuaishou).KuaishouGlobalProjectConcurrency);

            viewModel.TikTokProjectConcurrency = 4;
            viewModel.SaveCommand.Execute(null);
            viewModel.Load();
            Assert.Equal(4m, viewModel.TikTokProjectConcurrency);
            Assert.Equal(8m, viewModel.KuaishouProjectConcurrency);
            Assert.Equal(5, ClientSettingsStore.Load(tikTok).TiktokUploadConcurrency);
            Assert.Equal(6, ClientSettingsStore.Load(kuaishou).TiktokUploadConcurrency);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            temporary.Delete(recursive: true);
        }
    }
}
