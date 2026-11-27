using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Persistence;
using PlatformPublisher.Weixin.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;
using static PlatformPublisher.Weixin.Publishing.WeixinSeriesFields;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinSeriesFormServiceTests
{
    [Fact]
    public async Task SavedProjectFormFeedsBrowserAndApiWithoutChangingSource()
    {
        using var fixture = new Fixture();
        var preset = new WeixinSeriesPreset
        {
            Name = "漫剧",
            Values = new() { [DramaType] = "自动", [TrialSeconds] = "80", [Producer] = "方案制作方", [EpisodeMode] = "全部" },
        };
        fixture.Store.Save("first", new() { DefaultId = preset.Id, Presets = [preset] });
        fixture.Store.SaveProject("first", fixture.Directory, new(preset.Id, new() { [Producer] = "当前项目制作方" }, PresetSnapshot: preset));
        var form = fixture.Store.LoadProject("first", fixture.Directory);
        Assert.Null(fixture.Store.LoadProject("second", fixture.Directory));
        Assert.Empty(fixture.Store.Load("second").Presets);

        var job = fixture.NewJob();
        var preview = await fixture.Service.PreviewAsync(job, form, CancellationToken.None);
        Assert.Empty(preview.Errors);
        Assert.Equal("当前项目制作方", PreviewValue(preview, Producer).Value);
        Assert.Equal("本项目覆盖", PreviewValue(preview, Producer).Source);
        Assert.Equal("漫剧", PreviewValue(preview, DramaType).Value);
        Assert.Equal("4", PreviewValue(preview, FreeEpisodes).Value);
        Assert.Contains("100 秒", preview.TrialSummary);
        Assert.All(preview.Episodes, item => Assert.True(item.Selected));

        var source = await File.ReadAllTextAsync(fixture.Config);
        fixture.Store.SaveAccountForm("first", new() { [FreeEpisodes] = "3", [Producer] = "新的账号制作方" });
        var effective = await fixture.Service.PrepareUploadAsync(job, null, CancellationToken.None);
        Assert.NotNull(effective);
        var browser = new WeixinSeriesConfigOverrideService(fixture.Directory).Prepare(job, effective);
        Assert.Equal(4, browser!.SelectedVideoCount);
        var api = await WeixinSeriesApiProject.LoadAsync(effective!, job.ProjectName,
            new WeixinPublishOptions { SeriesUploadMode = WeixinSeriesUploadMode.ApiSubmit, EpisodeSelectionMode = "range" },
            1, CancellationToken.None);
        Assert.Equal("当前项目制作方", api.Form!.Producer);
        Assert.Equal(4, api.Form.TrialCount);
        Assert.Equal(4, api.EpisodeCount);
        Assert.Equal(source, await File.ReadAllTextAsync(fixture.Config));
        var otherAccount = fixture.NewJob();
        otherAccount.AccountId = "second";
        Assert.Null(await fixture.Service.PrepareUploadAsync(otherAccount, null, CancellationToken.None));
    }

    [Fact]
    public async Task PreviewReportsMissingMaterialsAndExplicitEpisodes()
    {
        using var fixture = new Fixture();
        fixture.Store.SaveAccountForm("first", new()
        {
            [EpisodeMode] = "指定集数", [EpisodeIndexes] = "2,6-7", [Identity] = "剧目发行方",
            [Qualification] = "普通微短剧", [AiOwnershipMode] = "启用",
        });
        var preview = await fixture.Service.PreviewAsync(fixture.NewJob(), null, CancellationToken.None);
        Assert.Contains(preview.Errors, error => error.Contains("6、7"));
        Assert.Contains(preview.Errors, error => error.Contains("备案号"));
        Assert.Contains(preview.Errors, error => error.Contains("授权证明"));
        Assert.Contains(preview.Errors, error => error.Contains("AI 软件权属证明"));
        Assert.Equal(2, Assert.Single(preview.Episodes, item => item.Selected).Number);
    }

    [Fact]
    public async Task CancellingThePreviewLeavesNoProjectConfigurationChanges()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.PreviewAsync(fixture.NewJob(), null, cancellation.Token));
        Assert.Null(fixture.Store.LoadProject("first", fixture.Directory));
    }

    [Fact]
    public void AccountFormReadsThePriorDefaultAndAnExplicitClearDoesNotRestoreIt()
    {
        using var fixture = new Fixture();
        var legacyDefault = new WeixinSeriesPreset { Values = new() { [Producer] = "原账号默认制作方" } };
        var unusedPreset = new WeixinSeriesPreset { Values = new() { [Producer] = "未启用的制作方" } };
        fixture.Store.Save("first", new() { DefaultId = legacyDefault.Id, Presets = [unusedPreset, legacyDefault] });
        fixture.Store.Save("second", new() { Presets = [unusedPreset] });

        Assert.Equal("原账号默认制作方", fixture.Store.LoadAccountForm("first")[Producer]);
        Assert.Empty(fixture.Store.LoadAccountForm("second"));
        fixture.Store.SaveAccountForm("first", []);
        Assert.Empty(fixture.Store.LoadAccountForm("first"));
        Assert.Equal(legacyDefault.Id, fixture.Store.Load("first").DefaultId);
    }

    [Fact]
    public async Task AccountFormsAreIsolatedAndNewProjectFormsInheritTheLatestSavedValues()
    {
        using var fixture = new Fixture();
        fixture.Store.SaveAccountForm("first", new() { [Producer] = "账号甲", [DramaType] = "漫剧" });
        fixture.Store.SaveAccountForm("second", new() { [Producer] = "账号乙", [DramaType] = "真人" });
        var first = fixture.NewJob();
        var second = fixture.NewJob();
        second.AccountId = "second";
        var project = new WeixinSeriesProjectForm("", new() { [Title] = "项目剧名" });
        fixture.Store.SaveProject(first.AccountId, first.ProjectDirectory, project);

        var firstPreview = await fixture.Service.PreviewAsync(first, project, CancellationToken.None);
        var secondPreview = await fixture.Service.PreviewAsync(second, null, CancellationToken.None);
        Assert.Equal("账号甲", PreviewValue(firstPreview, Producer).Value);
        Assert.Equal("账号配置", PreviewValue(firstPreview, Producer).Source);
        Assert.Equal("项目剧名", PreviewValue(firstPreview, Title).Value);
        Assert.Equal("账号乙", PreviewValue(secondPreview, Producer).Value);
        Assert.Equal("测试剧", PreviewValue(secondPreview, Title).Value);

        fixture.Store.SaveAccountForm("first", new() { [Producer] = "账号甲更新", [DramaType] = "漫剧" });
        var effective = await fixture.Service.PrepareUploadAsync(first, null, CancellationToken.None);
        Assert.NotNull(effective);
        var api = await WeixinSeriesApiProject.LoadAsync(effective!, first.ProjectName,
            new WeixinPublishOptions { SeriesUploadMode = WeixinSeriesUploadMode.ApiSubmit },
            1, CancellationToken.None);
        Assert.Equal("账号甲更新", api.Form!.Producer);
        Assert.Equal("账号乙", fixture.Store.LoadAccountForm("second")[Producer]);
    }

    private static WeixinSeriesFormValue PreviewValue(WeixinSeriesFormPreview preview, string key) =>
        Assert.Single(preview.Fields, item => item.Key == key);

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = System.IO.Directory.CreateTempSubdirectory("weixin-series-preset-");
        public string Directory => _root.FullName;
        public string Config => Path.Combine(Directory, "weixin-channel-submit.json");
        public WeixinSeriesPresetStore Store { get; }
        public WeixinSeriesFormService Service { get; }

        public Fixture()
        {
            Store = new WeixinSeriesPresetStore(new AccountJsonSettingStore(new PlatformDatabase(Path.Combine(Directory, "app.db"))));
            Service = new WeixinSeriesFormService(Store, new FakeProbe(), new UnusedProjectParser(), EmptyAiRuntimeSettingsProvider.Instance, Directory);
            File.WriteAllText(Path.Combine(Directory, "简介.txt"), "剧集类型：漫剧\n");
            foreach (var file in new[] { "poster.jpg", "prove.png", "report.png", "第1集.mp4", "第2集.mp4", "第3集.mp4", "第4集.mp4" })
                File.WriteAllText(Path.Combine(Directory, file), "fixture");
            File.WriteAllText(Config, """
                {
                  "first_page": { "actions": [
                    { "type": "fill", "label": "剧目名称", "value": "测试剧" },
                    { "type": "fill", "label": "剧目简介", "value": "测试简介" },
                    { "type": "fill", "label": "制作方名称", "value": "原制作公司" },
                    { "type": "fill", "label": "总集数", "value": "4" },
                    { "type": "fill", "label": "试看集数", "value": "3" },
                    { "type": "fill", "label": "制作成本", "value": "12" },
                    { "type": "choose", "field_label": "剧目类型", "option_text": "真人" },
                    { "type": "upload", "label": "剧目海报", "paths": ["poster.jpg"] },
                    { "type": "upload", "label": "剧目制作证明材料", "paths": ["prove.png"] },
                    { "type": "upload", "label": "剧目资质", "paths": ["report.png"] },
                    { "type": "set_checked", "label": "我已知悉并同意", "enabled": true }
                  ] },
                  "second_page": { "upload": { "paths": ["第1集.mp4","第2集.mp4","第3集.mp4","第4集.mp4"] } }
                }
                """);
        }

        public PublishJob NewJob() => new()
        {
            AccountId = "first", ProjectDirectory = Directory, ProjectName = "测试剧",
            PlatformOptionsJson = new WeixinPublishOptions().ToJson(),
        };

        public void Dispose() => _root.Delete(recursive: true);
    }

    private sealed class FakeProbe : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory,
            CancellationToken cancellationToken) => Task.FromResult(new ExternalProcessResult(0, "25", ""));
    }

    private sealed class UnusedProjectParser : IProjectInfoParser
    {
        public Task<ProjectInfo> ParseAsync(string projectDir, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
