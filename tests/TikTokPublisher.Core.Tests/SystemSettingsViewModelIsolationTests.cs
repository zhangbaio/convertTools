using TikTokPublisher.Core.Services;
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
            HghighStandardDeviceId = "standard-device",
            HghighStandardClientExe = "standard.exe",
            HghighEdition = "standard"
        };

        Assert.Equal("standard-device", viewModel.HghighActiveDeviceId);
        Assert.Equal("standard.exe", viewModel.HghighActiveClientExe);

        var settings = viewModel.ToSettings();
        Assert.Equal("shared@example.test", settings.HghighAccount);
        Assert.Equal("shared-password", settings.HghighPassword);
        Assert.Equal("high-device", settings.HghighDeviceId);
        Assert.Equal("standard-device", settings.HghighStandardDeviceId);
        Assert.Equal("standard", settings.HghighEdition);
    }
}
