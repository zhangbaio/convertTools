using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Ui.ViewModels;
using Microsoft.Data.Sqlite;

namespace TikTokPublisher.Core.Tests;

public sealed class SystemSettingsViewModelIsolationTests
{
    [Fact]
    public void CustomDatabasePathIsUsedForLoadAndSave()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "platform-settings-tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(tempRoot, "platform-settings.db");
        try
        {
            var viewModel = new SystemSettingsViewModel(databasePath);
            viewModel.Load();
            viewModel.AiTextEndpoint = "https://platform-settings.example/v1";
            viewModel.AiTextModel = "platform-model";

            viewModel.SaveSettingsCommand.Execute(null);

            var saved = ClientSettingsStore.Load(databasePath);
            Assert.Equal("https://platform-settings.example/v1", saved.AiTextEndpoint);
            Assert.Equal("platform-model", saved.AiTextModel);
            Assert.Equal(Path.GetFullPath(databasePath), viewModel.MainDatabasePath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneralSettingsSaveCommitsProjectConcurrencyAndBusinessSettingsTogether()
    {
        var tempRoot = Directory.CreateTempSubdirectory("general-settings-");
        var databasePath = Path.Combine(tempRoot.FullName, "settings.db");
        try
        {
            ClientSettingsStore.Update(settings =>
            {
                settings.TiktokGlobalProjectConcurrency = 3;
                settings.TiktokProofKeepDocx = false;
            }, databasePath);

            var viewModel = new SystemSettingsViewModel(databasePath);
            viewModel.Load();
            viewModel.TiktokProofKeepDocx = true;
            viewModel.ProjectConcurrencySettings.TikTokProjectConcurrency = 2.5m;
            ClientSettings? applied = null;
            viewModel.ProjectConcurrencySettings.TikTokLimitSaved += settings => applied = settings;

            await viewModel.SaveSettingsCommand.ExecuteAsync(true);

            Assert.False(viewModel.LastSaveSucceeded);
            Assert.Contains("项目并发", viewModel.SaveMessage);
            Assert.Null(applied);
            var saved = ClientSettingsStore.Load(databasePath);
            Assert.Equal(3, saved.TiktokGlobalProjectConcurrency);
            Assert.False(saved.TiktokProofKeepDocx);

            viewModel.ProjectConcurrencySettings.TikTokProjectConcurrency = 4;
            await viewModel.SaveSettingsCommand.ExecuteAsync(true);

            Assert.True(viewModel.LastSaveSucceeded);
            saved = ClientSettingsStore.Load(databasePath);
            Assert.Equal(4, saved.TiktokGlobalProjectConcurrency);
            Assert.True(saved.TiktokProofKeepDocx);
            Assert.NotNull(applied);
            Assert.Equal(4, applied.TiktokGlobalProjectConcurrency);
            Assert.True(applied.TiktokProofKeepDocx);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void DefaultConstructorKeepsTikTokDatabasePath()
    {
        var viewModel = new SystemSettingsViewModel();

        Assert.Equal(ClientSettingsStore.MainDatabasePath, viewModel.MainDatabasePath);
    }

    [Fact]
    public void HongguoEditionSharesCredentialsButKeepsDeviceAndExeProfilesSeparate()
    {
        var viewModel = new SystemSettingsViewModel
        {
            HghighAccount = "shared@example.test",
            HghighPassword = "shared-password",
            HghighDeviceId = "high-device",
            HghighClientExe = "high.exe",
            HghighClientVersion = "2.1.6",
            HghighStandardDeviceId = "standard-device",
            HghighStandardClientExe = "standard.exe",
            HghighStandardClientVersion = "2.2.1",
            HghighEdition = "standard"
        };

        Assert.Equal("standard-device", viewModel.HghighActiveDeviceId);
        Assert.Equal("standard.exe", viewModel.HghighActiveClientExe);
        Assert.Equal("2.2.1", viewModel.HghighActiveClientVersion);

        viewModel.HghighEdition = "high";
        Assert.Equal("2.1.6", viewModel.HghighActiveClientVersion);
        viewModel.HghighActiveClientVersion = "";
        viewModel.HghighEdition = "standard";

        var settings = viewModel.ToSettings();
        Assert.Equal("shared@example.test", settings.HghighAccount);
        Assert.Equal("shared-password", settings.HghighPassword);
        Assert.Equal("high-device", settings.HghighDeviceId);
        Assert.Equal("standard-device", settings.HghighStandardDeviceId);
        Assert.Equal("2.1.6", settings.HghighClientVersion);
        Assert.Equal("2.2.1", settings.HghighStandardClientVersion);
        Assert.Equal("standard", settings.HghighEdition);
    }
}
