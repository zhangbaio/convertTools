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
    public void Submit_body_posts_the_series_id()
    {
        TikTokApiSeriesDocument.BuildSubmitBody("7707915752204129298")
            .Should().Contain("\"contractId\":\"7707915752204129298\"");
    }
}
