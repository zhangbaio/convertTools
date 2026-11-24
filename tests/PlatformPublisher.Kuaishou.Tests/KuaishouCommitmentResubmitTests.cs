using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouCommitmentResubmitTests
{
    [Theory]
    [InlineData("承诺函-以下资质未按格式撰写承诺函，未加盖机构主体公章")]
    [InlineData("承诺函:以下资质未按格式撰写承诺函，未加盖机构主体公章")]
    public void Warning_matches_the_commitment_seal_sentence(string text)
    {
        Assert.True(KuaishouCommitmentResubmit.IsCommitmentSealWarning(text));
    }

    [Fact]
    public void General_daily_limit_banner_is_not_the_commitment_warning()
    {
        const string banner = "平台现已对每日短剧上传数量进行优化调整，对每位用户每日上传数量进行限制。";
        Assert.False(KuaishouCommitmentResubmit.IsCommitmentSealWarning(banner));
    }

    [Fact]
    public void Listed_published_series_with_the_warning_is_selected()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "result": 1,
              "data": {
                "totalCount": 2,
                "data": [
                  {
                    "miniSeriesId": 50626878,
                    "courseName": "春禾持善把家兴",
                    "sellingStatus": 1,
                    "auditStatus": 3,
                    "failReasonSummary": { "reviewReason": "承诺函-以下资质未按格式撰写承诺函，未加盖机构主体公章" }
                  },
                  {
                    "miniSeriesId": 50848123,
                    "courseName": "七零重生共致富",
                    "sellingStatus": 1,
                    "auditStatus": 3,
                    "failReasonSummary": { "reviewReason": "" }
                  }
                ]
              }
            }
            """);
        var matches = new List<KuaishouCommitmentResubmit.Target>();
        var raw = KuaishouCommitmentResubmit.ReadPage(document.RootElement, matches, out var total);

        Assert.Equal(2, raw);
        Assert.Equal(2, total);
        var target = Assert.Single(matches);
        Assert.Equal("50626878", target.SeriesId);
        Assert.Equal("春禾持善把家兴", target.Title);
    }

    [Fact]
    public void Series_that_is_not_on_shelf_is_left_out()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "result": 1,
              "data": {
                "totalCount": 1,
                "data": [
                  {
                    "miniSeriesId": "1",
                    "courseName": "未上架",
                    "sellingStatus": 2,
                    "auditStatus": 3,
                    "checkReasonDisplayInfo": { "rejectReason": "承诺函:以下资质未按格式撰写承诺函，未加盖机构主体公章" }
                  }
                ]
              }
            }
            """);
        var matches = new List<KuaishouCommitmentResubmit.Target>();
        KuaishouCommitmentResubmit.ReadPage(document.RootElement, matches, out _);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task List_walks_every_page_and_keeps_only_the_warning()
    {
        var gateway = new ScriptGateway(
            ("https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/queryList", Page(1, "春禾持善把家兴", 11, true)),
            ("https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/queryList", Page(2, "七零重生共致富", 22, false)));

        var found = await new KuaishouCommitmentResubmitService()
            .ListAsync(gateway, progress: null, CancellationToken.None, pageSize: 1);

        var target = Assert.Single(found);
        Assert.Equal("11", target.SeriesId);
        Assert.Contains("\"sellingStatus\":1", gateway.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"auditStatus\":3", gateway.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_continues_and_the_daily_cap_stops_the_rest()
    {
        var calls = 0;
        var gateway = new ScriptGateway(_ =>
        {
            calls++;
            if (calls == 1)
                return """{"result":11,"error_msg":"封面不能为空"}""";
            return """{"result":11,"error_msg":"为保证内容审核质量，每位创作者每日限传10部"}""";
        });
        var targets = new[]
        {
            new KuaishouCommitmentResubmit.Target("1", "第一部", "承诺函"),
            new KuaishouCommitmentResubmit.Target("2", "第二部", "承诺函"),
            new KuaishouCommitmentResubmit.Target("3", "第三部", "承诺函"),
        };

        var result = await new KuaishouCommitmentResubmitService()
            .SubmitAsync(gateway, targets, progress: null, CancellationToken.None);

        Assert.Equal(0, result.Submitted);
        Assert.Equal(1, result.Failed);
        Assert.True(result.StoppedByDailyLimit);
        Assert.Equal(2, calls);
        Assert.Contains(result.Lines, line => line.Contains("第一部", StringComparison.Ordinal) && line.Contains("失败", StringComparison.Ordinal));
        Assert.Contains("今日的短剧上传已达上限，其余 1 部未再提交。", result.Lines);
        Assert.Contains("\"miniSeriesId\":\"2\"", gateway.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_keeps_the_current_content_and_submits_only_after_the_platform_allows_it()
    {
        var gateway = new ScriptGateway((url, _) =>
        {
            if (url.EndsWith("/baseInfo", StringComparison.Ordinal))
                return BaseInfo();
            if (url.EndsWith("/episodeList", StringComparison.Ordinal))
                return Episodes();
            if (url.EndsWith("/update", StringComparison.Ordinal))
                return """{"result":1,"data":{"canModifyVideo":true}}""";
            return """{"result":1}""";
        });
        var targets = new[] { new KuaishouCommitmentResubmit.Target("50626878", "春禾持善把家兴", "承诺函") };

        var result = await new KuaishouCommitmentResubmitService()
            .SubmitAsync(gateway, targets, progress: null, CancellationToken.None);

        Assert.Equal(1, result.Submitted);
        Assert.Equal(0, result.Failed);
        var updateText = gateway.Bodies.Single(body => body.Contains("copyrightMaterialValidationFlag", StringComparison.Ordinal));
        using var update = JsonDocument.Parse(updateText);
        var saved = update.RootElement;
        Assert.Equal("春禾持善把家兴", saved.GetProperty("miniSeriesTitle").GetString());
        Assert.Equal("原简介。", saved.GetProperty("description").GetString());
        Assert.Equal("cover-key", saved.GetProperty("coverImgKey").GetString());
        Assert.Equal(100, saved.GetProperty("priceCent").GetInt32());
        Assert.Equal("1", saved.GetProperty("unlockStart").GetString());
        Assert.Equal("project-key", saved.GetProperty("copyrightProofMaterialInfoList")[0].GetProperty("fileKeyList")[0].GetString());
        Assert.Equal("letter-key", saved.GetProperty("copyrightProofMaterialInfoList")[0].GetProperty("commitmentLetterKeyList")[0].GetString());
        Assert.Equal(88, saved.GetProperty("episodes")[0].GetProperty("episodeId").GetInt32());
        Assert.Equal(0, saved.GetProperty("episodes")[0].GetProperty("priceCent").GetInt32());
        Assert.Equal("{\"miniSeriesId\":\"50626878\"}", gateway.Bodies[^1]);
        Assert.EndsWith("/submit", gateway.Urls[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saved_series_that_does_not_need_review_is_not_submitted()
    {
        var gateway = new ScriptGateway((url, _) =>
        {
            if (url.EndsWith("/baseInfo", StringComparison.Ordinal))
                return BaseInfo();
            if (url.EndsWith("/episodeList", StringComparison.Ordinal))
                return Episodes();
            return """{"result":1,"data":{"canModifyVideo":false}}""";
        });

        var result = await new KuaishouCommitmentResubmitService().SubmitAsync(
            gateway,
            [new KuaishouCommitmentResubmit.Target("50626878", "春禾持善把家兴", "承诺函")],
            progress: null,
            CancellationToken.None);

        Assert.Equal(0, result.Submitted);
        Assert.Equal(1, result.Failed);
        Assert.DoesNotContain(gateway.Urls, url => url.EndsWith("/submit", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.Contains("不需要重新审核", StringComparison.Ordinal));
    }

    private static string BaseInfo() =>
        """
        {"result":1,"data":{"miniSeriesId":50626878,"courseName":"春禾持善把家兴","courseDescription":"原简介。","coverImgKey":"cover-key","price":100,"saleType":3,"updateEnd":true,"hasFilingNo":false,"filingNo":"（浙）微短剧备字（2026）第1号","filingNoType":1,"unlockCount":0,"unlockStart":1,"smallAmountUnlock":false,"miniSeriesTagIdList":[{"tagId":3,"tagName":"都市"}],"productionCost":1.5,"episodeAvgTime":1,"directorList":null,"mainActorList":[{"actorName":"甲","actorRole":"乙"}],"producer":null,"productionOrg":"原机构","productionOrgProvince":null,"productionLicenseNo":null,"revolutionHistorySpecialTheme":false,"checkNewFilingParamFlag":true,"copyrightProofType":1,"copyrightProofMaterialInfoVoList":[{"materialType":4,"fileList":[{"fileKey":"project-key"}],"commitmentLetterList":[{"fileKey":"letter-key"}]}],"authorizedCopyrightMaterialList":[],"syncProfile":true,"seriesContentType":2,"productMethod":3,"authorDeclaration":1,"screenwriterInfoList":[{"name":"编剧"}],"directorInfoList":[{"name":"导演"}],"producerInfo":{"name":"制片"},"displayInfoList":[{"displayPlatform":"快手","displayChannel":"原频道","displayDate":1}],"productionForm":"竖屏","showSdpaFieldsEditEntrance":false}}
        """;

    private static string Episodes() =>
        """
        {"result":1,"data":{"seriesList":[{"episodeId":88,"episodeTitle":"春禾持善把家兴","coverImgKey":"episode-cover","free":true,"price":null}]}}
        """;

    private static string Page(int page, string title, int id, bool warning)
    {
        var reason = warning ? "承诺函-以下资质未按格式撰写承诺函，未加盖机构主体公章" : "";
        return $$"""
            {
              "result": 1,
              "data": {
                "totalCount": 2,
                "currentPage": {{page}},
                "data": [
                  {
                    "miniSeriesId": {{id}},
                    "courseName": "{{title}}",
                    "sellingStatus": 1,
                    "auditStatus": 3,
                    "failReasonSummary": { "reviewReason": "{{reason}}" }
                  }
                ]
              }
            }
            """;
    }

    private sealed class ScriptGateway : IKuaishouKdjGateway
    {
        private readonly Func<string, string, string>? _route;
        private readonly Queue<(string Url, string Body)> _script = new();
        private readonly Func<string, string>? _submit;

        public List<string> Bodies { get; } = [];
        public List<string> Urls { get; } = [];

        public ScriptGateway(params (string Url, string Body)[] pages)
        {
            foreach (var page in pages)
                _script.Enqueue(page);
        }

        public ScriptGateway(Func<string, string> submit) => _submit = submit;

        public ScriptGateway(Func<string, string, string> route) => _route = route;

        public Task<string> GetAsync(string url, CancellationToken cancellationToken)
        {
            if (url.Contains("teacher/info", StringComparison.Ordinal))
                return Task.FromResult("""{"result":1,"data":{"bizIdentity":3}}""");
            return Task.FromResult(
                """
                {"result":1,"data":{"enableCopyrightValidation":true,"enableTubeSupportIap":true,"enableAuthorDeclaration":true,"enableScreenwriterAttributeFlag":true,"enableCopyrightProofNewAttribute":true,"enableNewSdpaFieldsEdit":false,"enableNovelToAnimalSeries":false}}
                """);
        }

        public Task<string> PostJsonAsync(string url, string body, CancellationToken cancellationToken)
        {
            Urls.Add(url);
            Bodies.Add(body);
            if (_route is not null)
                return Task.FromResult(_route(url, body));
            if (_submit is not null)
                return Task.FromResult(_submit(body));
            if (_script.Count == 0)
                throw new InvalidOperationException("没有更多列表页。");
            var next = _script.Dequeue();
            Assert.Equal(next.Url, url);
            return Task.FromResult(next.Body);
        }

        public Task<string> UploadFileAsync(string url, string path, string label, int byteLimit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
