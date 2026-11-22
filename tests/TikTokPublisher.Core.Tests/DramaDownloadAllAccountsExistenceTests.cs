using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class DramaDownloadAllAccountsExistenceTests
{
    [Fact]
    public async Task Lists_every_matching_account_and_combines_sources_under_the_same_name()
    {
        var root = Directory.CreateTempSubdirectory("drama-existence-all-");
        try
        {
            var archive = root.CreateSubdirectory("archive");
            var first = Account("first", "账号一", root.FullName, archive.FullName);
            var second = Account("second", "账号二", root.FullName, archive.FullName);
            var third = Account("third", "账号三", root.FullName, archive.FullName);
            WriteArchive(archive, "first-project", "《同一部剧》", first.Id);
            WriteArchive(archive, "second-project", "同一部剧", second.Id);
            WriteArchive(archive, "unassigned-project", "旧归档", "");
            WriteArchive(archive, "kuaishou-project", "快手剧", "", source: "kuaishou");

            var checkedAccounts = new ConcurrentBag<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await DramaDownloadExistenceService.CheckAllAsync(
                ["同一部剧", "另一部剧"], [first, second, third, first], (titles, account, _) =>
                {
                    titles.Should().Equal("同一部剧", "另一部剧");
                    checkedAccounts.Add(account!.Id);
                    var keys = account.Id == first.Id ? new HashSet<string> { "同一部剧" } : [];
                    return Task.FromResult((true, "", keys));
                }, timeout.Token);

            checkedAccounts.Should().BeEquivalentTo(new[] { "first", "second", "third" });
            var label = result.LabelFor("《同一部剧》");
            label.Should().Be("已存在 · 账号一（已归档、管理系统）、账号二（已归档）");
            DramaDownloadExistenceResult.ToneFor(label).Should().Be(DramaExistenceTone.Present);
            result.LabelFor("另一部剧").Should().Be("不存在");
            result.LabelFor("旧归档").Should().Be("已存在 · 归属未标记的归档");
            result.LabelFor("快手剧").Should().Be("不存在");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_account_keeps_other_matches_and_leaves_unverified_absence_unknown()
    {
        var accounts = new[] { Account("first", "账号一"), Account("second", "账号二") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await DramaDownloadExistenceService.CheckAllAsync(
            ["命中剧"], accounts, (_, account, _) =>
                account?.Id == "first"
                    ? Task.FromException<(bool, string, HashSet<string>)>(new TaskCanceledException())
                    : Task.FromResult((true, "", new HashSet<string> { "命中剧" })), timeout.Token);

        result.Accounts[0].Result.ManagementMessage.Should().Be("连接管理系统超时");
        result.LabelFor("命中剧").Should().Be("已存在 · 账号二（管理系统） · 部分来源未核对");
        DramaDownloadExistenceResult.ToneFor(result.LabelFor("未命中剧")).Should().Be(DramaExistenceTone.Unknown);
    }

    [Fact]
    public async Task Canceling_the_search_cancels_the_account_queries()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = DramaDownloadExistenceService.CheckAllAsync(
            ["剧名"], [Account("first", "账号一")], async (_, _, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return (true, "", new HashSet<string>());
            }, cancellation.Token);

        await started.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    private static TikTokAccountProfile Account(string id, string name, string workspace = "", string archive = "") =>
        new()
        {
            Id = id,
            TiktokAccountNickname = name,
            TiktokUploadProfilePath = workspace,
            TiktokArchiveRootDir = archive,
        };

    private static void WriteArchive(DirectoryInfo archive, string key, string title, string owner, string source = "tiktok")
    {
        var meta = archive.CreateSubdirectory("meta");
        File.WriteAllText(Path.Combine(meta.FullName, key + ".json"), JsonSerializer.Serialize(new
        {
            archiveSource = source,
            originalTitle = title,
            projectKey = key,
            accountProfileId = owner,
        }));
    }
}
