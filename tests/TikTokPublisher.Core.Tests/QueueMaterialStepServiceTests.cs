using FluentAssertions;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueMaterialStepServiceTests
{
    [Fact]
    public async Task Concurrent_uploaded_episode_fallback_is_coalesced_per_project()
    {
        var root = Path.Combine(Path.GetTempPath(), $"coalesced-web-fallback-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "恢复项目_版权恢复");
        Directory.CreateDirectory(source);
        try
        {
            var item = new QueueProjectItem
            {
                ProjectDir = source,
                NewTitle = "恢复项目",
                EpisodeCount = 1,
            };
            var calls = 0;
            RoleReferenceEpisodeFallback fallback = async (_, episodes, _, ct) =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(100, ct);
                var cache = ProjectVideoResolver.ResolvePublishedMaterialVideoDirectory(source);
                Directory.CreateDirectory(cache);
                var path = Path.Combine(cache, "第001集.mp4");
                File.WriteAllBytes(path, [1, 2, 3]);
                return new Dictionary<int, string> { [episodes.Single()] = path };
            };

            var tasks = Enumerable.Range(0, 2).Select(_ =>
                QueueMaterialStepService.EnsureRoleReferenceEpisodeVideosAsync(
                    item,
                    new ClientSettings(),
                    [1],
                    _ => { },
                    CancellationToken.None,
                    fallback));
            var results = await Task.WhenAll(tasks);

            calls.Should().Be(1);
            results.Should().OnlyContain(result => result.ContainsKey(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Role_reference_fallback_merges_only_existing_requested_episode_files()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"role-web-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var episode10 = Path.Combine(tempDir, "第010集.mp4");
            File.WriteAllBytes(episode10, [1, 2, 3]);
            var resolved = new Dictionary<int, string>();
            var fallback = new Dictionary<int, string>
            {
                [10] = episode10,
                [11] = Path.Combine(tempDir, "missing.mp4"),
                [12] = episode10,
            };

            var added = QueueMaterialStepService.MergeRoleReferenceFallbackVideos(
                resolved,
                [10, 11],
                fallback);

            added.Should().Be(1);
            resolved.Should().ContainSingle().Which.Key.Should().Be(10);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Role_reference_video_lookup_uses_web_fallback_when_archived_project_has_no_book_id()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"role-web-no-book-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(testRoot, "source");
        var fallbackDir = Path.Combine(testRoot, "web-fallback");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(fallbackDir);
        try
        {
            var fallbackVideo = Path.Combine(fallbackDir, "web-第010集.mp4");
            File.WriteAllBytes(fallbackVideo, [1, 2, 3]);
            var fallbackCalls = 0;
            var logs = new List<string>();
            var item = new QueueProjectItem
            {
                ProjectDir = sourceDir,
                NewTitle = "恢复项目",
                EpisodeCount = 20,
            };

            var resolved = await QueueMaterialStepService.EnsureRoleReferenceEpisodeVideosAsync(
                item,
                new ClientSettings(),
                [10],
                logs.Add,
                CancellationToken.None,
                (_, episodes, _, _) =>
                {
                    fallbackCalls++;
                    episodes.Should().Equal(10);
                    return Task.FromResult<IReadOnlyDictionary<int, string>>(
                        new Dictionary<int, string> { [10] = fallbackVideo });
                });

            fallbackCalls.Should().Be(1);
            resolved[10].Should().Be(fallbackVideo);
            logs.Should().Contain(message => message.Contains("缺少 bookId", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("TikTok 已上传视频兜底", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("第10集.mp4", 10)]
    [InlineData("episode-11.mp4", 11)]
    [InlineData("show_ep_012.mp4", 12)]
    [InlineData("0013.mp4", 13)]
    [InlineData("show-14.mp4", 14)]
    public void Role_reference_episode_parser_accepts_common_file_names(string fileName, int expected)
    {
        QueueMaterialStepService.TryReadEpisodeNumberFromFileName(fileName, out var actual)
            .Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public void Role_reference_video_lookup_maps_complete_unlabeled_series_by_natural_order()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"role-video-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        try
        {
            File.WriteAllBytes(Path.Combine(sourceDir, "clip-a.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(sourceDir, "clip-b.mp4"), [2]);

            var resolved = QueueMaterialStepService.ResolveExistingRoleReferenceEpisodeVideos(
                sourceDir,
                [1, 2],
                expectedEpisodeCount: 2);

            Path.GetFileName(resolved[1]).Should().Be("clip-a.mp4");
            Path.GetFileName(resolved[2]).Should().Be("clip-b.mp4");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public void Role_reference_video_lookup_does_not_guess_from_partial_unlabeled_series()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"role-video-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        try
        {
            File.WriteAllBytes(Path.Combine(sourceDir, "clip-a.mp4"), [1]);

            var resolved = QueueMaterialStepService.ResolveExistingRoleReferenceEpisodeVideos(
                sourceDir,
                [1],
                expectedEpisodeCount: 60);

            resolved.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public void Role_reference_video_lookup_reuses_upload_staging_when_source_is_incomplete()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"role-video-staging-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "source");
        var stagingDir = Path.Combine(
            workspaceDir,
            "workflow",
            "source",
            TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);
        try
        {
            File.WriteAllBytes(Path.Combine(sourceDir, "第1集.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(stagingDir, "renamed-第2集.mp4"), [2]);

            var resolved = QueueMaterialStepService.ResolveExistingRoleReferenceEpisodeVideos(
                sourceDir,
                [1, 2],
                expectedEpisodeCount: 2);

            Path.GetFileName(resolved[1]).Should().Be("第1集.mp4");
            Path.GetFileName(resolved[2]).Should().Be("renamed-第2集.mp4");
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public void Download_completeness_uses_successful_download_count_instead_of_stale_declared_count()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"download-real-count-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var item = new QueueProjectItem { ProjectDir = sourceDir, EpisodeCount = 68 };

        try
        {
            File.WriteAllText(
                Path.Combine(sourceDir, "shortdrama-project.json"),
                JsonSerializer.Serialize(new { episodeCount = 68 }));
            File.WriteAllText(
                Path.Combine(sourceDir, ".weixin-channel-download-state.json"),
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    video_count = 37,
                    episodes = "all",
                    failures = Array.Empty<string>(),
                    episode_number_mode = "source",
                    episode_mappings = Enumerable.Range(1, 37).Select(number => new
                    {
                        source_episode_number = number,
                        sequence_episode_number = number,
                    }),
                }));
            foreach (var number in Enumerable.Range(1, 37))
                File.WriteAllBytes(Path.Combine(sourceDir, $"第{number}集.mp4"), [1]);

            var expectedNumbers = QueueMaterialStepService.ResolveExpectedDownloadedEpisodeNumbers(sourceDir, item);
            expectedNumbers.Should().Equal(Enumerable.Range(1, 37));
            QueueMaterialStepService.InspectDownloadedEpisodes(sourceDir, item, expectedNumbers)
                .IsComplete.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public void Download_completeness_uses_source_episode_mapping_when_numbers_are_not_contiguous()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"download-mapping-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var sourceNumbers = Enumerable.Range(1, 7).Concat(Enumerable.Range(9, 33)).ToArray();
        var item = new QueueProjectItem
        {
            ProjectDir = sourceDir,
            EpisodeCount = 40,
        };

        try
        {
            File.WriteAllText(
                Path.Combine(sourceDir, "shortdrama-project.json"),
                JsonSerializer.Serialize(new { episodeCount = 40 }));
            File.WriteAllText(
                Path.Combine(sourceDir, ".weixin-channel-download-state.json"),
                JsonSerializer.Serialize(new
                {
                    episodes = "all",
                    episode_number_mode = "source",
                    episode_mappings = sourceNumbers.Select((number, index) => new
                    {
                        source_episode_number = number,
                        sequence_episode_number = index + 1,
                    }),
                }));
            foreach (var number in sourceNumbers)
                File.WriteAllBytes(Path.Combine(sourceDir, $"第{number}集.mp4"), [1, 2, 3]);

            var expectedNumbers = QueueMaterialStepService.ResolveExpectedDownloadedEpisodeNumbers(sourceDir, item);
            expectedNumbers.Should().Equal(sourceNumbers);

            var complete = QueueMaterialStepService.InspectDownloadedEpisodes(sourceDir, item, expectedNumbers);
            complete.IsComplete.Should().BeTrue();
            complete.Expected.Should().Be(40);
            complete.FoundCount.Should().Be(40);
            complete.Missing.Should().BeEmpty();

            File.Delete(Path.Combine(sourceDir, "第9集.mp4"));
            var missing = QueueMaterialStepService.InspectDownloadedEpisodes(sourceDir, item, expectedNumbers);
            missing.IsComplete.Should().BeFalse();
            missing.FoundCount.Should().Be(39);
            missing.Missing.Should().Equal(9);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public void NeedsAiRewrite_requires_matching_persisted_synopsis_mode_and_content()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"rewrite-state-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "原项目");
        var workflowDir = Path.Combine(workspace, "workflow", "原项目");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        var infoPath = Path.Combine(workflowDir, "短剧信息.txt");
        File.WriteAllText(infoPath, """
            原剧名: 旧梦归途
            新剧名: 归途有光
            推荐语: 她跨越风雨，终于找回被夺走的人生。
            简介: 女主在家族变故后重整旗鼓，与伙伴携手揭开多年前的真相。
            """);

        var item = new QueueProjectItem
        {
            ProjectDir = sourceDir,
            OriginalTitle = "旧梦归途",
            NewTitle = "归途有光",
        };
        var disabled = new TikTokAccountProfile
        {
            Id = "account-a",
            TiktokAiRewriteSynopsis = false,
        };
        var enabled = new TikTokAccountProfile
        {
            Id = "account-a",
            TiktokAiRewriteSynopsis = true,
        };

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceDir);

            QueueMaterialStepService.NeedsAiRewrite(item, disabled).Should().BeFalse();
            QueueMaterialStepService.PersistRewriteCompletionState(context, disabled, infoPath, rewriteSynopsis: false);

            QueueMaterialStepService.NeedsAiRewrite(item, enabled).Should().BeTrue(
                "enabling synopsis rewrite must not reuse a title-only completion");

            QueueMaterialStepService.PersistRewriteCompletionState(context, enabled, infoPath, rewriteSynopsis: true);
            QueueMaterialStepService.NeedsAiRewrite(item, enabled).Should().BeFalse();

            ProjectWorkspaceService.UpdateProjectInfoField(infoPath, "简介", "后来手工恢复的原始简介内容");
            QueueMaterialStepService.NeedsAiRewrite(item, enabled).Should().BeTrue(
                "the persisted synopsis fingerprint no longer matches the project info");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { /* SQLite can retain a pooled Windows handle briefly. */ }
        }
    }

    [Fact]
    public void Persisted_ai_result_with_colon_in_synopsis_can_recover_missing_completion_state()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"rewrite-recovery-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "珍珠心软之第五年重逢，驰先生再度失控");
        var workflowDir = Path.Combine(workspace, "workflow", "珍珠心软之第五年重逢，驰先生再度失控");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);

        const string originalTitle = "珍珠心软之第五年重逢，驰先生再度失控";
        const string newTitle = "四年重逢贺先生情难自禁";
        const string synopsis = "贺行舟与许念因误会分开，直到他得知当年真相：许念是为了保护他才独自离开。";
        var infoPath = Path.Combine(workflowDir, "短剧信息.txt");
        File.WriteAllText(infoPath, $"""
            原剧名: {originalTitle}
            新剧名: {newTitle}
            推荐语: 误会分散四年，重逢爱恨翻涌
            简介: {synopsis}
            """);

        var item = new QueueProjectItem
        {
            ProjectDir = sourceDir,
            OriginalTitle = originalTitle,
            NewTitle = newTitle,
        };
        var account = new TikTokAccountProfile
        {
            Id = "account-a",
            TiktokAiRewriteSynopsis = true,
        };

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceDir);
            var variantKey = $"{Path.GetFullPath(sourceDir)}#account-a#synopsis=1";
            var history = new[]
            {
                new AiRewriteHistoryRecord(
                    OriginalTitle: originalTitle,
                    OriginalSynopsis: "原始简介",
                    NewTitle: newTitle,
                    NewSynopsis: synopsis,
                    ProjectName: Path.GetFileName(sourceDir),
                    ProjectDir: sourceDir,
                    WorkspacePath: workspace,
                    AccountProfileId: account.Id,
                    AccountProfileName: "",
                    VariantKey: variantKey,
                    ModelName: "test-model",
                    CreatedAt: "2026-07-15T15:11:58"),
            };

            QueueMaterialStepService.NeedsAiRewrite(item, account).Should().BeTrue(
                "the generated file exists but its completion state was not written");
            QueueMaterialStepService.CurrentInfoMatchesRewriteHistory(
                    item,
                    context,
                    infoPath,
                    history,
                    rewriteSynopsis: true,
                    rewriteVariantKey: variantKey)
                .Should().BeTrue("the exact on-disk AI output is already recorded in history");
            QueueMaterialStepService.CurrentInfoMatchesRewriteHistory(
                    item,
                    context,
                    infoPath,
                    history,
                    rewriteSynopsis: true,
                    rewriteVariantKey: $"{Path.GetFullPath(sourceDir)}#account-b#synopsis=1")
                .Should().BeFalse("a different account variant must receive its own rewrite");
            QueueMaterialStepService.CurrentInfoMatchesRewriteHistory(
                    item,
                    context,
                    infoPath,
                    history.Select(record => record with { NewSynopsis = "另一个 AI 改写简介" }).ToArray(),
                    rewriteSynopsis: true,
                    rewriteVariantKey: variantKey)
                .Should().BeFalse("落盘简介必须与历史指纹完全一致");

            var persist = () => QueueMaterialStepService.PersistRewriteCompletionState(
                context,
                account,
                infoPath,
                rewriteSynopsis: true);
            persist.Should().NotThrow("简介正文中的全角冒号不应被当作字段分隔符");
            QueueMaterialStepService.NeedsAiRewrite(item, account).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { /* SQLite can retain a pooled Windows handle briefly. */ }
        }
    }
}
