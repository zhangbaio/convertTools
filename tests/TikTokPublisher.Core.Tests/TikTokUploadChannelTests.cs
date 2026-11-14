using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokUploadChannelTests
{
    [Theory]
    [InlineData("classic", "playwright", "classic")]
    [InlineData("batch", "playwright", "batch")]
    [InlineData("api", "api", "classic")]
    [InlineData("", "api", "classic")]
    [InlineData("playwright", "playwright", "batch")]
    public void Old_upload_values_map_onto_one_channel(string stored, string channel, string delivery)
    {
        var selection = TikTokUploadChannels.Resolve(
            stored,
            stored == "playwright" ? "batch" : "");

        selection.Channel.Should().Be(channel);
        selection.Delivery.Should().Be(delivery);
    }

    [Fact]
    public void Account_options_keep_batch_delivery_on_the_playwright_channel()
    {
        var options = TikTokPublishOptions.FromAccount(new TikTokAccountProfile
        {
            TiktokUploadStrategy = "batch",
        });

        options.UseApiUpload.Should().BeFalse();
        options.UsePlaywrightUpload.Should().BeTrue();
        options.UseBatchUpload.Should().BeTrue();
        options.UploadStrategy.Should().Be(TikTokUploadChannels.Playwright);
    }

    [Fact]
    public void Api_channel_does_not_use_page_batching()
    {
        var options = TikTokPublishOptions.FromAccount(new TikTokAccountProfile
        {
            TiktokUploadStrategy = "api",
            TiktokPlaywrightVideoDelivery = "batch",
        });

        options.UseApiUpload.Should().BeTrue();
        options.UseBatchUpload.Should().BeFalse();
    }

    [Fact]
    public void Series_update_keeps_existing_fields_and_writes_the_shell()
    {
        using var document = JsonDocument.Parse(
            """
            {"status":6,"licenseId":"lic-1","config":{"copyrightProof":{"commitmentSigned":true}}}
            """);
        var collection = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        var updated = TikTokApiSeriesDocument.Overlay(collection, new TikTokApiSeriesShell
        {
            Title = "重生赶海",
            Description = "简介",
            CoverUri = "tos/cover",
            TotalVideoNum = 82,
            AnchorPromotionEnabled = true,
            IsAiSeries = true,
            SourceLanguage = "zh",
            ThemeTagIds = ["7475195626620436486"],
            AudienceTagId = "female-id",
            ConsignmentEnabled = true,
            ProfilePreviewEpisodes = 3,
            FreePreviewEpisodes = 3,
            CopyrightFiles =
            [
                new TikTokApiCopyrightFile(2, "tos/proof", "证明材料.pdf"),
            ],
        });

        updated["status"]!.GetValue<int>().Should().Be(6);
        updated["title"]!.GetValue<string>().Should().Be("重生赶海");
        updated["coverUri"]!.GetValue<string>().Should().Be("tos/cover");
        updated["anchorPromotionStatus"]!.GetValue<int>().Should().Be(1);
        updated["config"]!["tags"]!["ShortDramaTheme"]![0]!.GetValue<string>().Should().Be("7475195626620436486");
        updated["config"]!["tags"]!["ShortDramaTargetAudience"]![0]!.GetValue<string>().Should().Be("female-id");
        updated["config"]!["contractSyncTag"]!["isAiSeries"]!.GetValue<bool>().Should().BeTrue();
        updated["config"]!["copyrightProof"]!["materials"]![0]!["materialType"]!.GetValue<int>().Should().Be(2);
        updated["config"]!["copyrightProof"]!["materials"]![0]!["files"]![0]!["fileName"]!.GetValue<string>()
            .Should().Be("证明材料.pdf");
    }

    [Fact]
    public void Audience_tag_id_comes_from_the_named_group()
    {
        using var document = JsonDocument.Parse(
            """
            {"data":{"ShortDramaTargetAudience":[{"id":"11","name":"女性"},{"id":"22","name":"男性"}]}}
            """);

        TikTokApiSeriesDocument.FindTagId(
                document.RootElement,
                "ShortDramaTargetAudience",
                ["女性", "Female"])
            .Should().Be("11");
    }

    [Fact]
    public void Audience_tag_id_comes_from_the_flat_tag_list()
    {
        using var document = JsonDocument.Parse(
            """
            {"data":{"tags":[
              {"id":"7473689406390484997","tag_group":"ShortDramaTargetAudience","tag_val":"Female"},
              {"id":"7473689406390403077","tag_group":"ShortDramaTargetAudience","tag_val":"Male"},
              {"id":"7475195943084703799","tag_group":"ShortDramaTheme","tag_val":"Ancient"}
            ]}}
            """);

        TikTokApiSeriesDocument.FindTagId(
                document.RootElement,
                "ShortDramaTargetAudience",
                ["女性", "女", "Female", "Women"])
            .Should().Be("7473689406390484997");
    }

    [Fact]
    public void Create_body_puts_the_business_contract_inside_contract()
    {
        using var document = JsonDocument.Parse(
            TikTokApiSeriesDocument.BuildCreateBody("请倾听孩子的心声", "7600123456789012345"));
        var contract = document.RootElement.GetProperty("contract");
        contract.GetProperty("title").GetString().Should().Be("请倾听孩子的心声");
        contract.GetProperty("contractId").GetString().Should().Be("7600123456789012345");
        document.RootElement.TryGetProperty("contractId", out _).Should().BeFalse();
    }

    [Fact]
    public void Create_body_sends_a_ct_contract_as_license_id()
    {
        using var document = JsonDocument.Parse(
            TikTokApiSeriesDocument.BuildCreateBody(
                "大婚日仙尊母亲归来",
                new TikTokApiSeriesDocument.TikTokSeriesContractBinding(null, "CT20260627700005")));
        var contract = document.RootElement.GetProperty("contract");
        contract.GetProperty("title").GetString().Should().Be("大婚日仙尊母亲归来");
        contract.TryGetProperty("contractId", out _).Should().BeFalse();
        document.RootElement.GetProperty("licenseId").GetString().Should().Be("CT20260627700005");
    }

    [Fact]
    public void License_list_accepts_the_ct_prefix_and_the_digits_after_it()
    {
        using var document = JsonDocument.Parse(
            """
            {"data":{"list":[
              {"licenseId":"CT20260627700005","title":"Content Licence Agreement","source":2},
              {"licenseId":"CT2024000000001","title":"另一份","source":2}
            ]}}
            """);

        TikTokApiSeriesDocument.IsPlatformContractId("20260627700005").Should().BeFalse();
        TikTokApiSeriesDocument.IsPlatformContractId("CT20260627700005").Should().BeFalse();
        TikTokApiSeriesDocument.MatchLicenseId(document.RootElement, "CT20260627700005")
            .Should().Be("CT20260627700005");
        TikTokApiSeriesDocument.MatchLicenseId(document.RootElement, "20260627700005")
            .Should().Be("CT20260627700005");
        TikTokApiSeriesDocument.MatchLicenseId(document.RootElement, "CT1999000000000")
            .Should().BeNull();
    }

    [Fact]
    public void License_overlay_keeps_the_series_id_and_writes_license_id()
    {
        var collection = System.Text.Json.Nodes.JsonNode.Parse(
            """{"contractId":"7707976863851009030","title":"旧标题"}""")!.AsObject();
        var updated = TikTokApiSeriesDocument.Overlay(collection, new TikTokApiSeriesShell
        {
            Title = "大婚日仙尊母亲归来",
            Description = "简介",
            TotalVideoNum = 64,
            LicenseId = "CT20260627700005",
            BusinessContractId = "CT20260627700005",
        });

        updated["licenseId"]!.GetValue<string>().Should().Be("CT20260627700005");
        updated["contractId"]!.GetValue<string>().Should().Be("7707976863851009030");
        updated.ContainsKey("contractedId").Should().BeFalse();
    }

    [Fact]
    public void Contract_list_skips_the_placeholder_and_reads_contracted_id()
    {
        using var document = JsonDocument.Parse(
            """
            {"data":{"contractList":[
              {"contractedId":"1","contractTitle":"占位"},
              {"contractedId":"7600123456789012345","contractTitle":"短剧合作合同"}
            ]}}
            """);

        var contract = TikTokApiSeriesDocument.ReadFirstBusinessContract(document.RootElement);
        contract.Should().NotBeNull();
        contract!.Value.Id.Should().Be("7600123456789012345");
        contract.Value.Title.Should().Be("短剧合作合同");
    }

    [Fact]
    public void Contract_code_maps_to_the_numeric_platform_id()
    {
        using var document = JsonDocument.Parse(
            """
            {"data":{"contractList":[
              {"contractedId":"7600123456789012345","contractTitle":"短剧合作","contractNo":"CT2024111100362"},
              {"contractedId":"7600999999999999999","contractTitle":"另一份","contractNo":"CT2024000000001"}
            ]}}
            """);

        TikTokApiSeriesDocument.IsPlatformContractId("CT2024111100362").Should().BeFalse();
        var match = TikTokApiSeriesDocument.MatchBusinessContract(document.RootElement, "CT2024111100362");
        match.Should().NotBeNull();
        match!.Value.Id.Should().Be("7600123456789012345");
        match.Value.Title.Should().Be("短剧合作");
    }

    [Fact]
    public void Unmatched_contract_code_is_not_guessed_from_several_contracts()
    {
        using var document = JsonDocument.Parse(
            """
            {"data":{"contractList":[
              {"contractedId":"7600123456789012345","contractTitle":"短剧合作"},
              {"contractedId":"7600999999999999999","contractTitle":"另一份"}
            ]}}
            """);

        TikTokApiSeriesDocument.MatchBusinessContract(document.RootElement, "CT2024111100362")
            .Should().BeNull();
    }

    [Fact]
    public void Publisher_ids_use_the_selectable_account_and_skip_hosted_creators()
    {
        using var document = JsonDocument.Parse(
            """
            {"data":{"creatorList":[
              {"creatorType":12,"accountInfo":{"uid":"7658121659320468494","nickname":"DramaReel","store_region":"US"}},
              {"creatorType":11,"accountInfo":{"uid":"7658121498233717767","nickname":"DramaReel","store_region":"EG"}},
              {"creatorType":11,"accountInfo":{"uid":"7658121615015986190","nickname":"DramaReel","store_region":"US"}},
              {"creatorType":11,"accountInfo":{"uid":"7670064863986598933","nickname":"DramaReel_th","store_region":"TH"}}
            ]}}
            """);

        var accounts = TikTokApiSeriesDocument.ReadPublisherAccounts(document.RootElement);
        var ids = TikTokApiSeriesDocument.SelectPublisherIds(
            accounts,
            customSelection: true,
            [
                TikTokPublishAccountCatalog.BuildKey("埃及", "DramaReel"),
                TikTokPublishAccountCatalog.BuildKey("美国", "DramaReel"),
                TikTokPublishAccountCatalog.BuildKey("泰国", "DramaReel_th"),
            ]);

        ids.Should().Equal("7658121498233717767", "7658121615015986190", "7670064863986598933");
        TikTokApiSeriesDocument.DescribePublisher(accounts[1])
            .Should().Be("埃及 / DramaReel");
    }

    [Fact]
    public void Overlay_writes_publisher_ids()
    {
        var updated = TikTokApiSeriesDocument.Overlay(null, new TikTokApiSeriesShell
        {
            Title = "大婚日仙尊母亲归来",
            PublisherIds = ["7658121498233717767", "7658121615015986190"],
        });

        var publishers = updated["publishers"]!.AsArray();
        publishers.Select(item => item!.GetValue<string>())
            .Should().Equal("7658121498233717767", "7658121615015986190");
    }

    [Fact]
    public void Overlay_marks_the_commitment_and_auto_publish_for_a_free_series()
    {
        var collection = System.Text.Json.Nodes.JsonNode.Parse(
            """{"config":{"publishMode":0,"price":{"priceInUsd":"12.32","enableSingleVideoPurchase":true}}}""")!.AsObject();
        var updated = TikTokApiSeriesDocument.Overlay(collection, new TikTokApiSeriesShell
        {
            Title = "大婚日仙尊母亲归来",
            ConsignmentEnabled = true,
            Signed = true,
            PublishMode = TikTokApiSeriesDocument.ResolvePublishMode("auto_after_review"),
        });

        updated["signed"]!.GetValue<bool>().Should().BeTrue();
        updated["paidExpireTime"]!.GetValue<string>().Should().Be("4102444800");
        updated["config"]!["publishMode"]!.GetValue<int>().Should().Be(1);
        updated["config"]!["price"]!.AsObject().Count.Should().Be(0);
        TikTokApiSeriesDocument.ResolvePublishMode("manual").Should().Be(2);
        TikTokApiSeriesDocument.ResolvePublishMode("scheduled").Should().Be(3);
    }

    [Fact]
    public void Submit_body_posts_the_series_id()
    {
        TikTokApiSeriesDocument.BuildSubmitBody("7707915752204129298")
            .Should().Contain("\"contractId\":\"7707915752204129298\"");
    }
}
