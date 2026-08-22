using System.Text.Json;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokReferenceSourcePackageServiceTests
{
    [Fact]
    public void ReusingCharacters_PreservesCompatibleReferencePairingManifest()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"preserve-role-manifest-{Guid.NewGuid():N}");
        var characterDirectory = Path.Combine(
            TikTokReferenceSourcePackageService.GetRoot(workflow),
            TikTokReferenceSourcePackageService.CharacterDirectoryName);
        Directory.CreateDirectory(characterDirectory);
        var characters = Enumerable.Range(1, 3)
            .Select(index => Path.Combine(characterDirectory, $"角色{index}.png"))
            .ToArray();
        var references = Enumerable.Range(1, 3)
            .Select(index => Path.Combine(workflow, $"参考{index}.jpg"))
            .ToArray();
        foreach (var path in characters) SaveSolidImage(path);
        foreach (var path in references) SaveSolidImage(path);
        var manifestPath = TikTokReferenceSourcePackageService.GetCharacterManifestPath(workflow);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                configuredCount = 3,
                minimumCount = 3,
                selectedCount = 3,
                characters = characters.Select((path, index) => new
                {
                    order = index + 1,
                    name = $"人物{index + 1}",
                    file = Path.GetFileName(path),
                    referencePath = references[index],
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
        var original = File.ReadAllText(manifestPath);

        try
        {
            var rewritten = TikTokReferenceSourcePackageService.WriteCharacterManifestIfNeeded(
                workflow,
                characterDirectory,
                characters.Select((path, index) => new TikTokReferenceSourcePackageService.GeneratedCharacter(
                    new TikTokReferenceSourcePackageService.CharacterProfile($"人物{index + 1}", "复用"),
                    path)).ToArray(),
                configuredCharacterCount: 3,
                candidateCount: 3,
                minimumCharacterCount: 3,
                preserveExisting: true);

            rewritten.Should().BeFalse();
            File.ReadAllText(manifestPath).Should().Be(original);
        }
        finally
        {
            if (Directory.Exists(workflow)) Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Inaccessible_reference_package_uses_stable_recovery_directory()
    {
        var evidence = Path.Combine(Path.GetTempPath(), "reference-evidence");

        var resolved = TikTokReferenceSourcePackageService.ResolveAccessiblePackageRoot(
            evidence,
            path => !path.EndsWith(
                TikTokReferenceSourcePackageService.DirectoryName,
                StringComparison.Ordinal));

        resolved.Should().Be(Path.Combine(
            Path.GetFullPath(evidence),
            TikTokReferenceSourcePackageService.RecoveryDirectoryName));
    }

    [Fact]
    public void Missing_or_accessible_reference_package_keeps_canonical_directory()
    {
        var evidence = Path.Combine(Path.GetTempPath(), "reference-evidence");

        TikTokReferenceSourcePackageService.ResolveAccessiblePackageRoot(evidence, _ => true)
            .Should().Be(Path.Combine(
                Path.GetFullPath(evidence),
                TikTokReferenceSourcePackageService.DirectoryName));
    }

    [Fact]
    public void Reset_package_root_clears_readonly_files_and_recreates_writable_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reference-reset-{Guid.NewGuid():N}");
        var characterDirectory = Path.Combine(root, TikTokReferenceSourcePackageService.CharacterDirectoryName);
        var oldFile = Path.Combine(characterDirectory, "旧角色.png");
        Directory.CreateDirectory(characterDirectory);
        File.WriteAllBytes(oldFile, [1, 2, 3]);
        File.SetAttributes(oldFile, File.GetAttributes(oldFile) | FileAttributes.ReadOnly);

        try
        {
            TikTokReferenceSourcePackageService.ResetPackageRoot(
                root,
                preserveCharactersAndRoleVector: false);

            Directory.Exists(root).Should().BeTrue();
            Directory.Exists(characterDirectory).Should().BeFalse();
            Directory.CreateDirectory(characterDirectory);
            var probe = Path.Combine(characterDirectory, "probe.txt");
            File.WriteAllText(probe, "ok");
            File.ReadAllText(probe).Should().Be("ok");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Role_reference_recovery_downloads_only_episodes_not_already_represented()
    {
        var retainedFrames = Enumerable.Range(1, 8)
            .Select(episode => $@"D:\frames\001_第{episode}集_0001.000秒_主体.jpg")
            .Append(@"D:\frames\补充_第09集_01.jpg")
            .ToArray();

        TikTokReferenceSourcePackageService.ResolveRoleReferenceRecoveryEpisodes(retainedFrames, 12)
            .Should().Equal(9, 10, 11, 12);
    }

    [Fact]
    public void Current_character_images_follow_manifest_order_without_rewriting_it()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"current-character-images-{Guid.NewGuid():N}");
        var characterDirectory = Path.Combine(
            TikTokReferenceSourcePackageService.GetRoot(workflow),
            TikTokReferenceSourcePackageService.CharacterDirectoryName);
        Directory.CreateDirectory(characterDirectory);
        var first = Path.Combine(characterDirectory, "甲.png");
        var second = Path.Combine(characterDirectory, "乙.png");
        var third = Path.Combine(characterDirectory, "丙.png");
        SaveSolidImage(first);
        SaveSolidImage(second);
        SaveSolidImage(third);
        var manifest = Path.Combine(
            characterDirectory,
            TikTokReferenceSourcePackageService.CharacterManifestFileName);
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            characters = new[]
            {
                new { order = 1, file = Path.GetFileName(second) },
                new { order = 2, file = Path.GetFileName(first) },
                new { order = 3, file = Path.GetFileName(third) },
            },
        }));
        var before = File.ReadAllText(manifest);

        try
        {
            TikTokReferenceSourcePackageService.ListCurrentCharacterImages(workflow, 3)
                .Should().Equal(second, first, third);
            File.ReadAllText(manifest).Should().Be(before);
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public async Task Default_scene_design_templates_are_installed_without_rendering()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scene-design-templates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, TikTokReferenceSourcePackageService.SceneDesignFileName1);
        var second = Path.Combine(root, TikTokReferenceSourcePackageService.SceneDesignFileName2);
        await File.WriteAllTextAsync(first, "stale");
        await File.WriteAllTextAsync(second, "stale");

        try
        {
            await TikTokReferenceSourcePackageService.InstallDefaultSceneDesignTemplatesAsync(
                root,
                CancellationToken.None);

            using var firstImage = Image.Load<Rgba32>(first);
            using var secondImage = Image.Load<Rgba32>(second);
            firstImage.Size.Should().Be(new Size(2435, 1011));
            secondImage.Size.Should().Be(new Size(2477, 1254));
            new FileInfo(first).Length.Should().BeGreaterThan(2_000_000);
            new FileInfo(second).Length.Should().BeGreaterThan(2_000_000);
            File.ReadAllBytes(first).Should().NotEqual(File.ReadAllBytes(second));

            await File.WriteAllTextAsync(first, "stale-again");
            await TikTokReferenceSourcePackageService.InstallDefaultSceneDesignTemplatesAsync(
                root,
                CancellationToken.None);
            Image.Identify(first)!.Width.Should().Be(2435);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Hidden_state_file_can_be_overwritten_during_force_rerun()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hidden-reference-state-{Guid.NewGuid():N}");
        var path = Path.Combine(root, TikTokReferenceSourcePackageService.StateFileName);
        try
        {
            await TikTokReferenceSourcePackageService.WriteHiddenStateFileAsync(
                path, "old", CancellationToken.None);
            await TikTokReferenceSourcePackageService.WriteHiddenStateFileAsync(
                path, "new", CancellationToken.None);

            File.ReadAllText(path).Should().Be("new");
            if (OperatingSystem.IsWindows())
                File.GetAttributes(path).Should().HaveFlag(FileAttributes.Hidden);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Character_profiles_are_extracted_from_reference_style_script()
    {
        const string script = """
                              人物设定
                              陆小满（女主）：23岁，清秀温婉，身穿素白连衣裙。
                              陆景琛（男主）：27岁，清冷沉稳的集团掌权人。
                              赵母（反派）：市井势利妇人，尖酸刻薄。

                              第一幕
                              陆小满：今天是我的订婚宴。
                              """;

        var profiles = TikTokReferenceSourcePackageService.ExtractCharacterProfiles(script);

        profiles.Select(x => x.Name).Should().Equal("陆小满", "陆景琛", "赵母");
        profiles.Should().OnlyContain(x => x.Description.Contains(x.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Character_prompt_requires_photorealistic_single_person_without_text()
    {
        var prompt = TikTokReferenceSourcePackageService.BuildCharacterPrompt(
            new TikTokReferenceSourcePackageService.CharacterProfile(
                "陆小满",
                "23岁中国女性，清秀温婉，身穿素白连衣裙。"));

        prompt.Should().Contain("真实真人影视剧演员定妆摄影");
        prompt.Should().Contain("画面中仅一人");
        prompt.Should().Contain("无文字、无Logo、无水印");
        prompt.Should().Contain("不是插画");
    }

    [Fact]
    public void Reference_character_prompt_makes_episode_identity_the_highest_priority()
    {
        var prompt = TikTokReferenceSourcePackageService.BuildReferenceCharacterPrompt(
            new TikTokReferenceSourcePackageService.CharacterProfile(
                "陆小满",
                "23岁中国女性，清秀温婉。"));

        prompt.Should().Contain("参考图是人物身份的唯一依据");
        prompt.Should().Contain("不得换脸");
        prompt.Should().Contain("不得重新选角");
        prompt.Should().Contain("完整显示头部、双手和双脚");
        prompt.Should().Contain("人物身份与参考图严格一致");
        prompt.Should().Contain("必须是成年女性");
        prompt.Should().Contain("款式、颜色、面料、纹样");
        prompt.Should().Contain("不得换装");
    }

    [Fact]
    public void Multi_angle_prompt_requires_four_distinct_locked_views()
    {
        var prompt = TikTokReferenceSourcePackageService.BuildMultiAngleCharacterPrompt("陆小满");

        prompt.Should().Contain("严格2×2等分");
        prompt.Should().Contain("左侧面");
        prompt.Should().Contain("右后方三分之四");
        prompt.Should().Contain("完整背面");
        prompt.Should().Contain("禁止四格都是正面");
        prompt.Should().Contain("服装款式、颜色、面料、纹样");
    }

    [Fact]
    public void Multi_angle_sheet_is_split_into_four_portrait_views()
    {
        var root = Path.Combine(Path.GetTempPath(), $"multi-angle-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var outputs = new[]
        {
            Path.Combine(root, "01.png"),
            Path.Combine(root, "02.png"),
            Path.Combine(root, "03.png"),
            Path.Combine(root, "04.png"),
        };
        var colors = new[]
        {
            new Rgba32(220, 30, 30),
            new Rgba32(30, 220, 30),
            new Rgba32(30, 30, 220),
            new Rgba32(220, 180, 30),
        };
        try
        {
            using var sheet = new Image<Rgba32>(600, 800, Color.Black);
            sheet.Mutate(context =>
            {
                context.Fill(colors[0], new Rectangle(0, 0, 300, 400));
                context.Fill(colors[1], new Rectangle(300, 0, 300, 400));
                context.Fill(colors[2], new Rectangle(0, 400, 300, 400));
                context.Fill(colors[3], new Rectangle(300, 400, 300, 400));
            });
            using var buffer = new MemoryStream();
            sheet.SaveAsPng(buffer);

            TikTokReferenceSourcePackageService.SplitMultiAngleSheet(
                buffer.ToArray(), outputs, CancellationToken.None);

            for (var index = 0; index < outputs.Length; index++)
            {
                using var view = Image.Load<Rgba32>(outputs[index]);
                view.Size.Should().Be(new Size(768, 1024));
                view[384, 512].Should().Be(colors[index]);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Paired_character_references_follow_character_manifest_order()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"paired-character-references-{Guid.NewGuid():N}");
        var characterDirectory = Path.Combine(
            TikTokReferenceSourcePackageService.GetRoot(workflow),
            TikTokReferenceSourcePackageService.CharacterDirectoryName);
        var referencesDirectory = Path.Combine(workflow, "抽帧原图");
        Directory.CreateDirectory(characterDirectory);
        Directory.CreateDirectory(referencesDirectory);
        var characters = Enumerable.Range(1, 3)
            .Select(index => Path.Combine(characterDirectory, $"主角{index}.png"))
            .ToArray();
        var references = Enumerable.Range(1, 3)
            .Select(index => Path.Combine(referencesDirectory, $"参考{index}.jpg"))
            .ToArray();
        foreach (var path in characters) SaveSolidImage(path);
        foreach (var path in references) SaveSolidImage(path);
        File.WriteAllText(
            TikTokReferenceSourcePackageService.GetCharacterManifestPath(workflow),
            JsonSerializer.Serialize(new
            {
                characters = characters.Select((path, index) => new
                {
                    order = index + 1,
                    file = Path.GetFileName(path),
                    referencePath = references[index],
                }),
            }));

        try
        {
            TikTokReferenceSourcePackageService.ResolvePairedCharacterReferences(workflow, characters)
                .Should().Equal(references.Select(Path.GetFullPath));
        }
        finally
        {
            if (Directory.Exists(workflow)) Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Role_reference_assignment_enforces_gender_and_distinct_people()
    {
        var profiles = new[]
        {
            new TikTokReferenceSourcePackageService.CharacterProfile("男主", "成年男性主角"),
            new TikTokReferenceSourcePackageService.CharacterProfile("女主", "成年女性主角"),
            new TikTokReferenceSourcePackageService.CharacterProfile("主要配角", "关键人物"),
        };
        var candidates = new[]
        {
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(1, "female", "woman-a", true, 96),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(2, "male", "man-a", true, 90),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(3, "male", "man-a", true, 98),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(4, "female", "woman-b", true, 88),
        };

        TikTokReferenceSourcePackageService.AssignRoleReferenceCandidates(profiles, candidates)
            .Should().Equal(3, 1, 4);
    }

    [Fact]
    public void Generic_leads_prefer_mixed_gender_but_keep_all_people_distinct()
    {
        var profiles = TikTokReferenceSourcePackageService.AddFallbackCharacters([], "古装权谋短剧");
        var candidates = new[]
        {
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(1, "female", "woman-a", true, 99),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(2, "female", "woman-b", true, 98),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(3, "male", "man-a", true, 80),
        };

        profiles.Select(profile => profile.Name).Should().Equal("主角1", "主角2", "主要配角");
        TikTokReferenceSourcePackageService.AssignRoleReferenceCandidates(profiles, candidates)
            .Should().Equal(1, 3, 2);
    }

    [Fact]
    public void NormalizeCharacterProfiles_Fills_Generic_Roles_To_Configured_Count()
    {
        var generic = new[]
        {
            new TikTokReferenceSourcePackageService.CharacterProfile("女主", "现代都市短剧女主角"),
            new TikTokReferenceSourcePackageService.CharacterProfile("男主", "现代都市短剧男主角"),
            new TikTokReferenceSourcePackageService.CharacterProfile("主要配角", "推动剧情发展的成年配角"),
        };

        var profiles = TikTokReferenceSourcePackageService.NormalizeCharacterProfiles(
            generic,
            "陆运程回乡报答恩人。",
            requiredCount: 3);

        profiles.Should().HaveCount(3);
        profiles.Select(profile => profile.Name).Should().Equal("主角1", "主角2", "主要配角");
    }

    [Fact]
    public void Generic_leads_allow_same_gender_when_no_mixed_pair_exists()
    {
        var profiles = TikTokReferenceSourcePackageService.AddFallbackCharacters([], "男性群像短剧");
        var candidates = new[]
        {
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(1, "male", "man-a", true, 99),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(2, "male", "man-b", true, 96),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(3, "male", "man-c", true, 92),
        };

        TikTokReferenceSourcePackageService.AssignRoleReferenceCandidates(profiles, candidates)
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Supporting_actor_rejects_body_detail_without_visible_face()
    {
        var profiles = TikTokReferenceSourcePackageService.AddFallbackCharacters([], "古装短剧");
        var candidates = new[]
        {
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(1, "female", "lead-a", true, 96),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(2, "male", "lead-b", true, 94),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(3, "unknown", "body-detail", true, 99, FaceVisible: false),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(4, "female", "support-c", true, 82),
        };

        TikTokReferenceSourcePackageService.AssignRoleReferenceCandidates(profiles, candidates)
            .Should().Equal(1, 2, 4);
    }

    [Fact]
    public void Supporting_actor_fails_instead_of_using_body_detail()
    {
        var profiles = TikTokReferenceSourcePackageService.AddFallbackCharacters([], "古装短剧");
        var candidates = new[]
        {
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(1, "female", "lead-a", true, 96),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(2, "male", "lead-b", true, 94),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(3, "unknown", "body-detail", true, 99, FaceVisible: false),
        };

        var action = () => TikTokReferenceSourcePackageService.AssignRoleReferenceCandidates(profiles, candidates);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*主要配角*清晰露脸*");
    }

    [Fact]
    public void Role_reference_assignment_fails_when_supporting_actor_duplicates_the_leads()
    {
        var profiles = new[]
        {
            new TikTokReferenceSourcePackageService.CharacterProfile("男主", "成年男性主角"),
            new TikTokReferenceSourcePackageService.CharacterProfile("女主", "成年女性主角"),
            new TikTokReferenceSourcePackageService.CharacterProfile("主要配角", "关键人物"),
        };
        var candidates = new[]
        {
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(1, "male", "lead-m", true, 95),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(2, "female", "lead-f", true, 95),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(3, "male", "lead-m", true, 85),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(4, "female", "lead-f", true, 85),
        };

        var action = () => TikTokReferenceSourcePackageService.AssignRoleReferenceCandidates(profiles, candidates);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*主要配角*不同*第三个人物*");
    }

    [Fact]
    public void Vision_candidate_set_combines_best_and_distributed_frames()
    {
        var sources = Enumerable.Range(1, 30).Select(index => $"frame-{index:D2}.jpg").ToArray();

        var selected = TikTokReferenceSourcePackageService.SelectVisionCandidatePaths(sources, 12);

        selected.Should().HaveCount(12);
        selected.Take(6).Should().Equal(sources.Take(6));
        selected.Should().Contain(path => path == "frame-25.jpg" || path == "frame-26.jpg");
    }

    [Theory]
    [InlineData(3, 18)]
    [InlineData(4, 20)]
    [InlineData(5, 24)]
    [InlineData(6, 24)]
    public void Vision_candidate_limit_scales_with_configured_roles(int roleCount, int expected)
    {
        TikTokReferenceSourcePackageService.ResolveVisionCandidateMaximum(roleCount)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(3, 16)]
    [InlineData(5, 16)]
    [InlineData(6, 16)]
    public void Vision_cross_batch_merge_is_bounded_to_avoid_timeout(int roleCount, int expected)
    {
        TikTokReferenceSourcePackageService.ResolveVisionMergeCandidateMaximum(roleCount)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    public void Vision_identity_discovery_uses_bounded_parallel_batches(int batchCount, int expected)
    {
        TikTokReferenceSourcePackageService.ResolveVisionIdentityBatchConcurrency(batchCount)
            .Should().Be(expected);
    }

    [Fact]
    public void Vision_prompt_requires_every_candidate_and_face_identity_comparison()
    {
        var prompt = TikTokReferenceSourcePackageService.BuildRoleReferenceSelectionPrompt(
            [new TikTokReferenceSourcePackageService.CharacterProfile("角色", "成年人物")],
            24);

        prompt.Should().Contain("#1 到 #24");
        prompt.Should().Contain("禁止遗漏");
        prompt.Should().Contain("服装相似不等于同一人");
        prompt.Should().Contain("面部身份不同");
        prompt.Should().Contain("clothing_visible");
        prompt.Should().Contain("clothing_clarity");
        prompt.Should().Contain("half_body");
    }

    [Fact]
    public void Batch_identity_representatives_keep_best_face_per_discovered_person()
    {
        var candidates = new[]
        {
            "人物甲_模糊.jpg", "人物甲_清晰脸.jpg", "人物乙.jpg", "人物甲_清晰服装.jpg", "背影.jpg",
        };
        var analyses = new[]
        {
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(1, "male", "P1", true, 65),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(2, "male", "P1", true, 96),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(3, "female", "P2", true, 88),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(
                4, "male", "P1", true, 84, ClothingVisible: true, ClothingClarity: 95, Framing: "half_body"),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(5, "unknown", "", true, 10, FaceVisible: false),
        };

        var selected = TikTokReferenceSourcePackageService.SelectBatchIdentityRepresentatives(candidates, analyses);

        selected.Should().HaveCount(3);
        selected.Should().Contain(["人物甲_清晰脸.jpg", "人物甲_清晰服装.jpg", "人物乙.jpg"]);
    }

    [Fact]
    public void Role_assignment_prefers_visible_clothing_over_face_closeup_for_same_person()
    {
        var profiles = new[]
        {
            new TikTokReferenceSourcePackageService.CharacterProfile("男主", "成年男性主角"),
        };
        var candidates = new[]
        {
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(
                1, "male", "P1", true, 99, ClothingVisible: false, ClothingClarity: 5, Framing: "close_up"),
            new TikTokReferenceSourcePackageService.ReferenceCandidateAnalysis(
                2, "male", "P1", true, 88, ClothingVisible: true, ClothingClarity: 92, Framing: "half_body"),
        };

        TikTokReferenceSourcePackageService.AssignRoleReferenceCandidates(profiles, candidates)
            .Should().Equal(2);
    }

    [Fact]
    public void Doubao_reference_generation_payload_contains_image_input_and_disables_group_generation()
    {
        var payload = TikTokReferenceSourcePackageService.BuildDoubaoReferenceImagePayload(
            "doubao-seedream-5-0-lite-260128",
            "保持人物身份一致",
            "data:image/jpeg;base64,AAAA",
            "1728x2304");

        payload["image"].Should().BeEquivalentTo(new[] { "data:image/jpeg;base64,AAAA" });
        payload["size"].Should().Be("1728x2304");
        payload["response_format"].Should().Be("b64_json");
        payload["watermark"].Should().Be(false);
        payload["sequential_image_generation"].Should().Be("disabled");
    }

    [Fact]
    public void Character_source_selection_prefers_single_person_extracted_frames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"character-source-selection-{Guid.NewGuid():N}");
        var workflow = Path.Combine(root, "workflow", "_短剧");
        var source = Path.Combine(root, "短剧");
        var extracted = Path.Combine(workflow, TikTokAiGenerationScreenshotService.RetainedFramesDirectoryName);
        var curated = Path.Combine(workflow, "角色设定");
        Directory.CreateDirectory(extracted);
        Directory.CreateDirectory(curated);
        var extractedSingle = Path.Combine(extracted, "清晰单人.png");
        var extractedMultiple = Path.Combine(extracted, "多人.png");
        var curatedSingle = Path.Combine(curated, "整理单人.png");
        SaveFaces(extractedSingle, 1);
        SaveFaces(extractedMultiple, 2);
        SaveFaces(curatedSingle, 1);

        try
        {
            var selected = TikTokReferenceSourcePackageService.FindEpisodeCharacterSources(
                new TikTokPublisher.Core.Queue.ProjectWorkspaceContext(source, workflow, root),
                Path.Combine(workflow, "项目原始资料", "参考格式原始素材包"));

            selected.Should().Equal(extractedSingle, curatedSingle, extractedMultiple);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        static void SaveFaces(string path, int count)
        {
            using var image = new Image<Rgba32>(160, 160, new Rgba32(35, 45, 60));
            var regions = count == 1
                ? new[] { (Left: 60, Right: 100) }
                : new[] { (Left: 25, Right: 55), (Left: 105, Right: 135) };
            foreach (var region in regions)
            for (var y = 24; y < 58; y++)
            for (var x = region.Left; x < region.Right; x++)
                image[x, y] = new Rgba32(210, 155, 125);
            image.SaveAsPng(path);
        }
    }

    [Fact]
    public void Character_profiles_fall_back_to_explicit_name_list_in_synopsis()
    {
        const string intro = "民间组织成员莫老头、邱胖子、沈秋三人率队携石像生赴滇西村落执行秘密任务。";

        var extracted = TikTokReferenceSourcePackageService.ExtractCharacterProfiles("", intro);

        // The public extractor deliberately returns only direct script matches. The package
        // generator subsequently enriches an undersized result from the synopsis list.
        extracted.Should().BeEmpty();
        var profiles = TikTokReferenceSourcePackageService.AddFallbackCharacters(extracted, intro);
        profiles.Take(3).Select(x => x.Name).Should().Equal("莫老头", "邱胖子", "沈秋");
    }

    [Fact]
    public void Explorer_capture_uses_reference_package_directories_when_package_is_current()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"reference-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var root = TikTokReferenceSourcePackageService.GetRoot(workflow);
            var character = Path.Combine(root, TikTokReferenceSourcePackageService.CharacterDirectoryName);
            var material = Path.Combine(root, TikTokReferenceSourcePackageService.MaterialDirectoryName, "001");
            var info = Path.Combine(root, "测试短剧");
            Directory.CreateDirectory(character);
            Directory.CreateDirectory(material);
            Directory.CreateDirectory(info);
            for (var index = 0; index < 3; index++)
                SaveSolidImage(Path.Combine(character, $"角色{index + 1}.png"));
            SaveSolidImage(Path.Combine(root, TikTokReferenceSourcePackageService.CharacterWorkbenchFileName));
            SaveSolidImage(Path.Combine(root, TikTokReferenceSourcePackageService.SceneDesignFileName1));
            SaveSolidImage(Path.Combine(root, TikTokReferenceSourcePackageService.SceneDesignFileName2));
            File.WriteAllText(
                Path.Combine(
                    TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflow),
                    TikTokReferenceSourcePackageService.StateFileName),
                "{}");
            File.WriteAllText(Path.Combine(material, "001-1.mp4.索引.txt"), "D:\\video.mp4");
            File.WriteAllText(Path.Combine(info, "shortdrama-project.json"), "{}");

            var outputs = Enumerable.Range(1, TikTokSourceFileInfoScreenshotService.RequiredImageCount)
                .Select(index => Path.Combine(workflow, $"{index}.png"))
                .ToArray();
            var requests = TikTokSourceFileInfoScreenshotService.BuildExplorerCaptureRequests(workflow, outputs);

            requests.Select(x => x.Directory).Should().Equal(root, character);
            requests.Select(x => x.LargeIcons).Should().Equal(false, true);
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Role_vector_renderer_replaces_only_declared_template_slots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"role-vector-template-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var characters = new[]
            {
                Path.Combine(root, "角色1.png"),
                Path.Combine(root, "角色2.png"),
            };
            SaveSolidImage(characters[0], new Rgba32(220, 30, 30));
            SaveSolidImage(characters[1], new Rgba32(30, 220, 30));
            var frame = Path.Combine(root, "成片参考.png");
            SaveSolidImage(frame, new Rgba32(220, 180, 30));
            var outputPath = Path.Combine(root, "角色矢量图.png");

            RoleVectorTemplateRenderer.Render(outputPath, characters, [frame]);

            using var output = Image.Load<Rgba32>(outputPath);
            var layout = RoleVectorTemplateRenderer.ResolveLayout(characters.Length);
            using var templateStream = typeof(TikTokReferenceSourcePackageService).Assembly
                .GetManifestResourceStream(layout.ResourceName)!;
            using var template = Image.Load<Rgba32>(templateStream);
            output.Width.Should().Be(RoleVectorTemplateRenderer.CanvasWidth);
            output.Height.Should().Be(RoleVectorTemplateRenderer.CanvasHeight);

            var mask = new bool[output.Width * output.Height];
            foreach (var slot in layout.Groups
                         .SelectMany(group => group.CharacterSlots.Concat(group.ReferenceSlots)))
            {
                for (var y = slot.Top; y < slot.Bottom; y++)
                for (var x = slot.Left; x < slot.Right; x++)
                    mask[y * output.Width + x] = true;
            }

            var outsideMismatchCount = 0;
            for (var y = 0; y < output.Height; y++)
            for (var x = 0; x < output.Width; x++)
            {
                if (!mask[y * output.Width + x] && output[x, y] != template[x, y])
                    outsideMismatchCount++;
            }
            outsideMismatchCount.Should().Be(0, "模板工具栏、节点连线和画布必须原样保留");

            var active = layout.Groups[0].CharacterSlots[0];
            output[active.X + active.Width / 2, active.Y + active.Height / 2].R.Should().BeGreaterThan(200);
            layout.Groups.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Role_vector_renderer_uses_distinct_character_views_in_slot_order()
    {
        var root = Path.Combine(Path.GetTempPath(), $"role-vector-multi-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var characters = new[] { Path.Combine(root, "角色1.png"), Path.Combine(root, "角色2.png") };
            SaveSolidImage(characters[0], new Rgba32(90, 90, 90));
            SaveSolidImage(characters[1], new Rgba32(50, 50, 50));
            var colors = new[]
            {
                new Rgba32(220, 30, 30),
                new Rgba32(30, 220, 30),
                new Rgba32(30, 30, 220),
                new Rgba32(220, 180, 30),
            };
            var views = colors.Select((color, index) => Path.Combine(root, $"视图{index + 1}.png")).ToArray();
            for (var index = 0; index < views.Length; index++) SaveSolidImage(views[index], colors[index]);
            var frame = Path.Combine(root, "参考.png");
            SaveSolidImage(frame, new Rgba32(120, 100, 80));
            var outputPath = Path.Combine(root, "角色矢量图.png");

            RoleVectorTemplateRenderer.Render(
                outputPath,
                characters,
                [frame],
                new IReadOnlyList<string>[] { views, new[] { characters[1] } });

            using var output = Image.Load<Rgba32>(outputPath);
            var slots = RoleVectorTemplateRenderer.TwoCharacterGroups[0].CharacterSlots;
            for (var index = 0; index < slots.Count; index++)
            {
                var slot = slots[index];
                output[slot.Left + slot.Width / 2, slot.Top + slot.Height / 2]
                    .Should().Be(colors[index]);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Role_vector_renderer_selects_dedicated_two_through_six_character_layouts()
    {
        var two = RoleVectorTemplateRenderer.ResolveLayout(2);
        var three = RoleVectorTemplateRenderer.ResolveLayout(3);
        var four = RoleVectorTemplateRenderer.ResolveLayout(4);
        var five = RoleVectorTemplateRenderer.ResolveLayout(5);
        var six = RoleVectorTemplateRenderer.ResolveLayout(6);

        two.ResourceName.Should().EndWith("RoleVectorTemplate2.png");
        two.Groups.Should().HaveCount(2);
        two.Groups.SelectMany(group => group.CharacterSlots)
            .Min(slot => slot.X).Should().BeGreaterThan(600, "双人布局应整体位于画布中部");

        three.ResourceName.Should().EndWith("RoleVectorTemplate3.png");
        three.Groups.Should().HaveCount(3);
        three.Groups.SelectMany(group => group.CharacterSlots)
            .Min(slot => slot.X).Should().BeGreaterThan(600, "三人布局应整体位于画布中部");

        four.ResourceName.Should().EndWith("RoleVectorTemplate4.png");
        four.Groups.Should().HaveCount(4);
        four.Groups.Take(3).SelectMany(group => group.CharacterSlots)
            .Max(slot => slot.Right).Should().BeLessThan(1200);
        four.Groups[3].CharacterSlots.Min(slot => slot.X)
            .Should().BeGreaterThan(1400, "第四个人物应独立位于右侧中部");

        five.ResourceName.Should().EndWith("RoleVectorTemplate5.png");
        five.Groups.Should().HaveCount(5);
        five.Groups.Take(3).SelectMany(group => group.CharacterSlots)
            .Max(slot => slot.Right).Should().BeLessThan(1200);
        five.Groups.Skip(3).Should().HaveCount(2);

        six.ResourceName.Should().EndWith("RoleVectorTemplate.png");
        six.Groups.Should().HaveCount(6);

        var tooFew = () => RoleVectorTemplateRenderer.ResolveLayout(1);
        var tooMany = () => RoleVectorTemplateRenderer.ResolveLayout(7);
        tooFew.Should().Throw<ArgumentOutOfRangeException>();
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Normalize_character_profiles_clamps_to_two_through_six_and_prioritizes_leads()
    {
        var tooFew = new[]
        {
            new TikTokReferenceSourcePackageService.CharacterProfile("甲", "普通人物"),
            new TikTokReferenceSourcePackageService.CharacterProfile("乙", "普通人物"),
        };
        TikTokReferenceSourcePackageService.NormalizeCharacterProfiles(tooFew, "甲乙共同经历危机。")
            .Should().HaveCount(2);

        var tooMany = Enumerable.Range(1, 7)
            .Select(index => new TikTokReferenceSourcePackageService.CharacterProfile(
                $"角色{index}",
                index == 7 ? "男主，故事核心" : "普通配角"))
            .ToArray();
        var selected = TikTokReferenceSourcePackageService.NormalizeCharacterProfiles(tooMany);

        selected.Should().HaveCount(6);
        selected[0].Name.Should().Be("角色7");
    }

    private static void SaveSolidImage(string path, Rgba32? color = null)
    {
        using var image = new Image<Rgba32>(64, 64, color ?? new Rgba32(80, 100, 120));
        image.SaveAsPng(path);
    }
}
