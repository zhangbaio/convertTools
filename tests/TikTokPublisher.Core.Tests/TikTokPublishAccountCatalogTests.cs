using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPublishAccountCatalogTests
{
    [Fact]
    public void Catalog_json_round_trips_and_deduplicates_accounts()
    {
        var json = TikTokPublishAccountCatalog.Serialize(
        [
            new TikTokPublishAccountOption(" 美国 ", "ShotTeller", "shot"),
            new TikTokPublishAccountOption("美国", "ShotTeller", "duplicate"),
            new TikTokPublishAccountOption("越南", "ReelSynth_vn", ""),
        ]);

        var accounts = TikTokPublishAccountCatalog.Parse(json);

        accounts.Should().Equal(
            new TikTokPublishAccountOption("美国", "ShotTeller", "shot"),
            new TikTokPublishAccountOption("越南", "ReelSynth_vn", ""));
    }

    [Fact]
    public void Retain_keeps_accounts_that_still_exist_and_drops_the_rest()
    {
        var catalog = new[]
        {
            new TikTokPublishAccountOption("美国", "ShotTeller", ""),
            new TikTokPublishAccountOption("越南", "ReelSynth_vn", ""),
        };
        var selected = new[]
        {
            TikTokPublishAccountCatalog.BuildKey("美国", "ShotTeller"),
            TikTokPublishAccountCatalog.BuildKey("日本", "ReelSynth_jp"),
            TikTokPublishAccountCatalog.BuildKey("美国", "ShotTeller"),
        };

        var retention = TikTokPublishAccountCatalog.Retain(selected, catalog);

        retention.KeptKeys.Should().Equal(TikTokPublishAccountCatalog.BuildKey("美国", "ShotTeller"));
        retention.DroppedKeys.Should().Equal(TikTokPublishAccountCatalog.BuildKey("日本", "ReelSynth_jp"));
    }

    [Fact]
    public void Disabled_custom_accounts_do_not_carry_a_selection_into_publish_options()
    {
        var account = new TikTokAccountProfile
        {
            TiktokCustomPublishAccountsEnabled = false,
            TiktokPublishAccountCatalogJson = TikTokPublishAccountCatalog.Serialize(
                [new TikTokPublishAccountOption("美国", "ShotTeller", "")]),
            TiktokSelectedPublishAccountKeys =
            [
                TikTokPublishAccountCatalog.BuildKey("美国", "ShotTeller"),
            ],
        };

        var options = TikTokPublishOptions.FromAccount(account);

        options.CustomPublishAccountsEnabled.Should().BeFalse();
        options.SelectedPublishAccountKeys.Should().BeEmpty();
    }

    [Fact]
    public void Enabled_custom_accounts_require_a_fetched_catalog_and_a_selection()
    {
        var missingCatalog = new TikTokAccountProfile { TiktokCustomPublishAccountsEnabled = true };
        var act = () => TikTokPublishConstants.ValidatePublishConfiguration(missingCatalog);
        act.Should().Throw<InvalidOperationException>().WithMessage("*获取已创建账号*");

        var unselected = new TikTokAccountProfile
        {
            TiktokCustomPublishAccountsEnabled = true,
            TiktokPublishAccountCatalogJson = TikTokPublishAccountCatalog.Serialize(
                [new TikTokPublishAccountOption("美国", "ShotTeller", "")]),
        };
        act = () => TikTokPublishConstants.ValidatePublishConfiguration(unselected);
        act.Should().Throw<InvalidOperationException>().WithMessage("*至少选择一个发布账号*");
    }

    [Fact]
    public void Selection_plan_checks_requested_accounts_unchecks_extras_and_reports_missing()
    {
        var shotTeller = TikTokPublishAccountCatalog.BuildKey("美国", "ShotTeller");
        var other = TikTokPublishAccountCatalog.BuildKey("美国", "user75433330142");
        var missing = TikTokPublishAccountCatalog.BuildKey("日本", "ReelSynth_jp");
        var visible = new[]
        {
            new TikTokPublishAccountVisibleOption("美国", "ShotTeller", "", Checked: false),
            new TikTokPublishAccountVisibleOption("美国", "user75433330142", "", Checked: true),
            new TikTokPublishAccountVisibleOption("越南", "ReelSynth_vn", "", Checked: false),
        };

        var plan = TikTokPublishAccountCatalog.Plan(visible, [shotTeller, missing]);

        plan.KeysToCheck.Should().Equal(shotTeller);
        plan.KeysToUncheck.Should().Equal(other);
        plan.MissingKeys.Should().Equal(missing);
    }
}
