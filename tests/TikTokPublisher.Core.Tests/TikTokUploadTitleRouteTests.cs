using FluentAssertions;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokUploadTitleRouteTests
{
    [Fact]
    public async Task Concurrent_operations_keep_separate_data_sources()
    {
        var provider = new ClientSettingsDramaSettingsProvider(new ClientSettings { DramaSourceChain = "playlet" });
        var overseasReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOverseasRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(5);

        using (ShortDramaDramaServices.HoldSettings(new ClientSettings { DramaSourceChain = "hongguoapp" }))
        {
            var overseasRead = Task.Run(async () =>
            {
                using var overseasRoute = ShortDramaDramaServices.HoldSettings(new ClientSettings { DramaSourceChain = "dramabox" });
                overseasReady.SetResult();
                await allowOverseasRead.Task.WaitAsync(timeout);
                return provider.Get().DramaSourceChain;
            });

            try
            {
                await overseasReady.Task.WaitAsync(timeout);
                provider.Get().DramaSourceChain.Should().Be("hongguoapp");
            }
            finally
            {
                allowOverseasRead.SetResult();
                (await overseasRead.WaitAsync(timeout)).Should().Be("dramabox");
            }
        }

        provider.Get().DramaSourceChain.Should().Be("playlet");
    }

    [Fact]
    public void Download_uses_the_current_chain_for_the_original_account_and_market()
    {
        var account = new TikTokAccountProfile { Id = "overseas", TiktokDramaMarket = TikTokAccountProfile.DramaMarketOverseas };
        var imported = new TikTokUploadTitleRoute(account.Id, account.TiktokDramaMarket, "dramabox", "dramabox:52");
        var settings = new ClientSettings
        {
            DramaSourceChain = "hongguoapp",
            OverseasDramaSourceChain = "foreign",
            ForeignServerUrl = "https://example.com",
            ForeignDeviceId = "device",
        };

        var activeRoute = QueueMaterialStepService.ResolveImportedAccountRoute(settings, account, imported);

        activeRoute.DramaSourceChain.Should().Be("foreign");
        activeRoute.DramaMarket.Should().Be(imported.DramaMarket);
        activeRoute.AccountProfileId.Should().Be(imported.AccountProfileId);
        imported.BookId.Should().Be("dramabox:52");
    }

    [Fact]
    public async Task Download_stops_before_fetching_when_imported_account_or_market_is_unavailable()
    {
        var directory = Directory.CreateTempSubdirectory("tiktok-download-route-");
        try
        {
            new TikTokUploadTitleRoute("overseas", TikTokAccountProfile.DramaMarketOverseas, "dramabox")
                .Save(directory.FullName, "dramabox:52");
            var item = new QueueProjectItem { ProjectDir = directory.FullName, AccountProfileId = "overseas" };
            var settings = new ClientSettings { DramaSourceChain = "hongguoapp" };
            var localAccount = new TikTokAccountProfile { Id = "overseas", TiktokDramaMarket = TikTokAccountProfile.DramaMarketLocal };

            var missing = () => QueueMaterialStepService.RunDownloadAsync(item, settings, null, false, _ => { }, CancellationToken.None);
            var changed = () => QueueMaterialStepService.RunDownloadAsync(item, settings, localAccount, false, _ => { }, CancellationToken.None);

            await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*找不到导入该剧时绑定的 TikTok 账号*");
            await changed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*绑定账号的剧集类型已改变*");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(TikTokAccountProfile.DramaMarketLocal, "hongguoapp")]
    [InlineData(TikTokAccountProfile.DramaMarketOverseas, "dramabox")]
    public void Capture_uses_the_saved_account_market_and_keeps_the_original_settings(string market, string source)
    {
        var settings = new ClientSettings
        {
            DramaSourceChain = "hongguoapp",
            OverseasDramaSourceChain = "dramabox",
            DramaBoxDeviceId = "device",
        };
        var account = new TikTokAccountProfile { Id = "account-a", Name = "任意账号名", TiktokDramaMarket = market };

        var route = TikTokUploadTitleRoute.Capture(settings, account);
        var effective = route.ApplyTo(settings);

        route.AccountProfileId.Should().Be(account.Id);
        route.DramaMarket.Should().Be(market);
        route.DramaSourceChain.Should().Be(source);
        effective.DramaSourceChain.Should().Be(source);
        settings.DramaSourceChain.Should().Be("hongguoapp");

        settings.OverseasDramaSourceChain = "foreign";
        route.ApplyTo(settings).DramaSourceChain.Should().Be(source);
    }

    [Fact]
    public void Capture_reports_invalid_or_incomplete_overseas_configuration()
    {
        var account = new TikTokAccountProfile { Id = "a", TiktokDramaMarket = TikTokAccountProfile.DramaMarketOverseas };
        var settings = new ClientSettings { DramaSourceChain = "hongguoapp", OverseasDramaSourceChain = "unknown" };

        var capture = () => TikTokUploadTitleRoute.Capture(settings, account);
        capture.Should().Throw<InvalidOperationException>().WithMessage("*海外剧数据链路配置无效*");

        settings.OverseasDramaSourceChain = "dramabox";
        settings.DramaBoxDeviceId = "";
        capture.Should().Throw<InvalidOperationException>().WithMessage("*DramaBox*设备ID*");
    }

    [Fact]
    public void Saved_route_retains_provider_identity_and_rejects_reuse_by_another_account()
    {
        var directory = Directory.CreateTempSubdirectory("tiktok-title-route-");
        try
        {
            var route = new TikTokUploadTitleRoute("a", TikTokAccountProfile.DramaMarketOverseas, "dramabox");
            var wrongId = () => route.Save(directory.FullName, "hongguoapp:102");
            wrongId.Should().Throw<InvalidDataException>();
            TikTokUploadTitleRoute.Read(directory.FullName).Should().BeNull();

            route.Save(directory.FullName, "dramabox:5201");
            TikTokUploadTitleRoute.Read(directory.FullName).Should().Be(route with { BookId = "dramabox:5201" });
            TikTokUploadTitleRoute.EnsureAccountOwnsProject(directory.FullName, "a");
            var validate = () => TikTokUploadTitleRoute.EnsureAccountOwnsProject(directory.FullName, "b");
            validate.Should().Throw<InvalidOperationException>().WithMessage("*另一个 TikTok 账号*");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
