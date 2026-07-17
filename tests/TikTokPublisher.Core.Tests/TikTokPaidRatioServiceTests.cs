using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

[CollectionDefinition(PaidRatioTestCollection.Name, DisableParallelization = true)]
public sealed class PaidRatioTestCollection
{
    public const string Name = "TikTokPaidRatio";
}

[Collection(PaidRatioTestCollection.Name)]
public sealed class TikTokPaidRatioServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _databasePath;
    private readonly string _legacyStatePath;

    public TikTokPaidRatioServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tiktok-paid-ratio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _databasePath = Path.Combine(_tempRoot, "client.db");
        _legacyStatePath = Path.Combine(_tempRoot, "legacy-paid-ratio-state.json");

        AppDatabaseInitializer.EnsureInitialized(_databasePath);
        TikTokPaidRatioService.DatabasePathOverride = () => _databasePath;
        TikTokPaidRatioService.LegacyStatePathOverride = () => _legacyStatePath;
        TikTokPaidRatioService.TodayKeyOverride = () => "2026-07-03";
    }

    public void Dispose()
    {
        TikTokPaidRatioService.DatabasePathOverride = null;
        TikTokPaidRatioService.LegacyStatePathOverride = null;
        TikTokPaidRatioService.TodayKeyOverride = null;
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static TikTokAccountProfile RatioAccount(double percent = 50.0, string id = "") =>
        new()
        {
            Id = id,
            TiktokPaidRatioEnabled = true,
            TiktokPaidRatioPercent = percent,
        };

    [Fact]
    public void DecidePaidForUpload_persists_state_to_database()
    {
        var account = RatioAccount();

        TikTokPaidRatioService.DecidePaidForUpload(account, databasePath: _databasePath).Should().BeFalse();

        AppSettingStore.TryLoadJson<PaidRatioStateFixture>(
            TikTokPaidRatioService.StateSettingKey,
            out var state,
            _databasePath).Should().BeTrue();
        state!.Accounts!["default"].Date.Should().Be("2026-07-03");
        state.Accounts["default"].Acc.Should().Be(0.5);
        state.Accounts["default"].Total.Should().Be(1);
        state.Accounts["default"].Paid.Should().Be(0);
        File.Exists(_legacyStatePath).Should().BeFalse();
    }

    [Fact]
    public void DecidePaidForUpload_resets_each_day()
    {
        AppSettingStore.SaveJson(
            TikTokPaidRatioService.StateSettingKey,
            new PaidRatioStateFixture
            {
                Accounts = new Dictionary<string, PaidRatioAccountFixture>
                {
                    ["default"] = new()
                    {
                        Date = "2026-07-02",
                        Acc = 0.5,
                        Total = 1,
                        Paid = 0,
                    },
                },
            },
            _databasePath);

        TikTokPaidRatioService.DecidePaidForUpload(RatioAccount(), databasePath: _databasePath).Should().BeFalse();

        AppSettingStore.TryLoadJson<PaidRatioStateFixture>(
            TikTokPaidRatioService.StateSettingKey,
            out var state,
            _databasePath).Should().BeTrue();
        state!.Accounts!["default"].Should().BeEquivalentTo(new PaidRatioAccountFixture
        {
            Date = "2026-07-03",
            Acc = 0.5,
            Total = 1,
            Paid = 0,
        });
    }

    [Fact]
    public void DecidePaidForUpload_twenty_percent_selects_four_of_twenty()
    {
        var account = RatioAccount(20.0);
        var decisions = Enumerable.Range(0, 20)
            .Select(_ => TikTokPaidRatioService.DecidePaidForUpload(account, databasePath: _databasePath))
            .ToList();

        decisions.Count(x => x).Should().Be(4);
        decisions
            .Select((paid, index) => paid ? index + 1 : 0)
            .Where(index => index > 0)
            .Should().Equal(5, 10, 15, 20);
    }

    [Fact]
    public void DecidePaidForUpload_isolates_state_by_account()
    {
        var accountA = RatioAccount(50.0, "account-a");
        var accountB = RatioAccount(50.0, "account-b");

        TikTokPaidRatioService.DecidePaidForUpload(accountA, databasePath: _databasePath).Should().BeFalse();
        TikTokPaidRatioService.DecidePaidForUpload(accountB, databasePath: _databasePath).Should().BeFalse();
        TikTokPaidRatioService.DecidePaidForUpload(accountA, databasePath: _databasePath).Should().BeTrue();

        AppSettingStore.TryLoadJson<PaidRatioStateFixture>(
            TikTokPaidRatioService.StateSettingKey,
            out var state,
            _databasePath).Should().BeTrue();
        state!.Accounts!["account-a"].Total.Should().Be(2);
        state.Accounts["account-a"].Paid.Should().Be(1);
        state.Accounts["account-b"].Total.Should().Be(1);
        state.Accounts["account-b"].Paid.Should().Be(0);
    }

    [Fact]
    public void DecidePaidForUpload_without_ratio_uses_paid_enabled()
    {
        var account = new TikTokAccountProfile
        {
            TiktokPaidRatioEnabled = false,
            TiktokPaidEnabled = true,
        };

        TikTokPaidRatioService.DecidePaidForUpload(account, databasePath: _databasePath).Should().BeTrue();
    }

    [Fact]
    public void DecidePaidForUpload_reuses_cached_decision_for_same_project()
    {
        var (_, workflowDir) = CreateProjectDirs();
        var account = RatioAccount();

        TikTokPaidRatioService.DecidePaidForUpload(account, workflowDir, databasePath: _databasePath).Should().BeFalse();
        File.WriteAllText(
            Path.Combine(workflowDir, "paid-decision.json"),
            JsonSerializer.Serialize(new { paid = true }));

        TikTokPaidRatioService.DecidePaidForUpload(account, workflowDir, databasePath: _databasePath).Should().BeFalse();
    }

    [Fact]
    public void DecidePaidForUpload_migrates_legacy_json_decision_to_database()
    {
        var (sourceDir, workflowDir) = CreateProjectDirs();
        var account = RatioAccount();
        File.WriteAllText(
            Path.Combine(workflowDir, "paid-decision.json"),
            JsonSerializer.Serialize(new { paid = true }));

        TikTokPaidRatioService.DecidePaidForUpload(account, workflowDir, databasePath: _databasePath).Should().BeTrue();
        File.Delete(Path.Combine(workflowDir, "paid-decision.json"));

        var payload = ProjectStateDocumentStore.LoadDocument(
            _tempRoot,
            sourceDir,
            TikTokPaidRatioService.PaidDecisionDocumentType);
        payload["paid"].GetBoolean().Should().BeTrue();
        TikTokPaidRatioService.DecidePaidForUpload(account, workflowDir, databasePath: _databasePath).Should().BeTrue();
    }

    private (string SourceDir, string WorkflowDir) CreateProjectDirs()
    {
        var sourceDir = Path.Combine(_tempRoot, "source");
        var workflowDir = Path.Combine(_tempRoot, "workflow", "_source");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        WorkspaceQueueDatabase.EnsureDatabase(WorkspaceQueuePaths.QueueDatabasePath(_tempRoot));

        var metadata = JsonSerializer.Serialize(new
        {
            projectKey = "source",
            title = "source",
            sourceProjectDir = sourceDir,
            workflowProjectDir = workflowDir,
            workflowDirName = "_source",
        });
        File.WriteAllText(Path.Combine(sourceDir, "shortdrama-project.json"), metadata);
        File.WriteAllText(Path.Combine(workflowDir, "shortdrama-project.json"), metadata);
        return (sourceDir, workflowDir);
    }

    private sealed class PaidRatioStateFixture
    {
        [JsonPropertyName("accounts")]
        public Dictionary<string, PaidRatioAccountFixture>? Accounts { get; set; }
    }

    private sealed class PaidRatioAccountFixture
    {
        [JsonPropertyName("date")]
        public string Date { get; set; } = "";

        [JsonPropertyName("acc")]
        public double Acc { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("paid")]
        public int Paid { get; set; }
    }
}

[Collection(PaidRatioTestCollection.Name)]
public sealed class TikTokUploadPrerequisiteServiceTests
{
    private static TikTokAccountProfile CompleteAccount() => new()
    {
        TiktokLoginEmail = "user@example.com",
        TiktokContractId = "CT123",
        TiktokContractIdMode = TikTokPublishConstants.ContractIdModeManual,
        TiktokExpectedFullPriceMode = "manual",
        TiktokExpectedFullPriceValue = "22.99",
    };

    [Fact]
    public void EnsureCommercialConfigValid_blocks_paid_ratio_without_price()
    {
        var account = CompleteAccount();
        account.TiktokPaidEnabled = false;
        account.TiktokPaidRatioEnabled = true;
        account.TiktokPaidRatioPercent = 40;
        account.TiktokExpectedFullPriceValue = "";

        var act = () => TikTokUploadPrerequisiteService.EnsureCommercialConfigValid(account);
        act.Should().Throw<InvalidOperationException>().WithMessage("*预期全集价格设置*");
    }

    [Fact]
    public void EnsureCommercialConfigValid_allows_free_mode_without_price()
    {
        var account = CompleteAccount();
        account.TiktokPaidEnabled = false;
        account.TiktokPaidRatioEnabled = false;
        account.TiktokExpectedFullPriceValue = "";

        var act = () => TikTokUploadPrerequisiteService.EnsureCommercialConfigValid(account);
        act.Should().NotThrow();
    }

    [Fact]
    public void PublishOptionsBuilder_applies_paid_ratio_decision()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tiktok-paid-ratio-builder-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppDatabaseInitializer.EnsureInitialized(databasePath);
            TikTokPaidRatioService.DatabasePathOverride = () => databasePath;
            TikTokPaidRatioService.LegacyStatePathOverride = () => Path.Combine(databasePath, "..", "legacy.json");
            TikTokPaidRatioService.TodayKeyOverride = () => "2026-07-03";

            var account = new TikTokAccountProfile
            {
                TiktokPaidEnabled = true,
                TiktokPaidRatioEnabled = true,
                TiktokPaidRatioPercent = 50,
            };

            TikTokPublishOptionsBuilder.FromAccount(account).PaidEnabled.Should().BeFalse();
            TikTokPublishOptionsBuilder.FromAccount(account).PaidEnabled.Should().BeTrue();
        }
        finally
        {
            TikTokPaidRatioService.DatabasePathOverride = null;
            TikTokPaidRatioService.LegacyStatePathOverride = null;
            TikTokPaidRatioService.TodayKeyOverride = null;
            try
            {
                if (File.Exists(databasePath))
                    File.Delete(databasePath);
            }
            catch
            {
                // ignore
            }
        }
    }
}
