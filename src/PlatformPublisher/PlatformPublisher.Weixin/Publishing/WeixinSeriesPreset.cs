namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinSeriesPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, string> Values { get; init; } = [];
}

public sealed record WeixinSeriesPresetCollection
{
    public string DefaultId { get; init; } = string.Empty;
    public List<WeixinSeriesPreset> Presets { get; init; } = [];
}

public sealed record WeixinSeriesProjectForm(string PresetId, Dictionary<string, string> Values, string ConfigPath = "",
    WeixinSeriesPreset? PresetSnapshot = null);

public enum WeixinSeriesFieldKind { Text, Choice, Multiline, Attachment, Attachments }

public sealed record WeixinSeriesField(string Key, string Label, string Group,
    WeixinSeriesFieldKind Kind = WeixinSeriesFieldKind.Text, string[]? Choices = null);

public static class WeixinSeriesFields
{
    private const string Rules = "默认发布规则";
    private const string Drama = "剧目信息";
    private const string Evidence = "声明与资质材料";
    private const string CostReport = "成本报表与制作方";
    private const string Videos = "分集视频";

    public const string DramaType = "剧目类型";
    public const string FreeEpisodes = "试看集数";
    public const string TrialSeconds = "试看秒数";
    public const string AiDeclared = "AI内容声明";
    public const string AiOwnershipMode = "上传 AI 软件权属证明";
    public const string AiOwnership = "AI软件权属证明";
    public const string AiProduction = "AI制作证明";
    public const string GenerateText = "生成简介和推荐语";
    public const string Title = "剧目名称";
    public const string Intro = "剧目简介";
    public const string Recommendation = "推荐语";
    public const string Total = "总集数";
    public const string Identity = "提审身份";
    public const string Qualification = "剧目资质";
    public const string Producer = "制作方名称";
    public const string Cost = "制作成本";
    public const string Report = "成本报表";
    public const string Permit = "备案号";
    public const string QualificationFile = "资质证明";
    public const string Production = "剧目制作证明材料";
    public const string Authorization = "版权采买&播出授权证明材料";
    public const string Actors = "演员资料";
    public const string ActorPhotos = "演员照片";
    public const string EpisodeMode = "分集选择";
    public const string EpisodeStart = "起始集数";
    public const string EpisodeEnd = "结束集数";
    public const string EpisodeIndexes = "指定集数";
    public const string GenerateCost = "生成成本报表";
    public const string Ratio = "演员总片酬占比";
    public const string Seal = "公章图片";
    public const string Signature = "法定代表人签名图片";
    public const string Representative = "法定代表人名称";
    public const string Monetization = "变现类型";
    public const string Poster = "剧目海报";
    public const string Promotion = "推广海报";

    public static IReadOnlyList<WeixinSeriesField> All { get; } =
    [
        new(DramaType, "剧集类型", Rules, WeixinSeriesFieldKind.Choice, ["自动", "漫剧", "真人", "数字真人"]),
        new(Monetization, Monetization, Rules, WeixinSeriesFieldKind.Choice, ["IAA广告变现", "IAP付费变现"]),
        new(FreeEpisodes, "免费集数", Rules),
        new(TrialSeconds, "试看秒数（0 使用免费集数）", Rules),
        new(Title, Title, Drama),
        new(Intro, Intro, Drama, WeixinSeriesFieldKind.Multiline),
        new(Recommendation, Recommendation, Drama),
        new(Total, Total, Drama),
        new(Poster, Poster, Drama, WeixinSeriesFieldKind.Attachment),
        new(Promotion, Promotion, Drama, WeixinSeriesFieldKind.Attachment),
        new(GenerateText, "发布信息 Agent", Drama, WeixinSeriesFieldKind.Choice, ["启用", "关闭"]),
        new(Producer, "制作方 / 报审机构", CostReport),
        new(Cost, "制作成本（万元）", CostReport),
        new(Report, "已有成本报表文件", CostReport, WeixinSeriesFieldKind.Attachment),
        new(GenerateCost, GenerateCost, CostReport, WeixinSeriesFieldKind.Choice, ["启用", "关闭"]),
        new(Ratio, "演员总片酬占比（%）", CostReport),
        new(Seal, Seal, CostReport, WeixinSeriesFieldKind.Attachment),
        new(Signature, Signature, CostReport, WeixinSeriesFieldKind.Attachment),
        new(Representative, Representative, CostReport),
        new(Identity, Identity, Evidence, WeixinSeriesFieldKind.Choice, ["剧目制作方", "剧目发行方", "版权方/授权播出方"]),
        new(Qualification, Qualification, Evidence, WeixinSeriesFieldKind.Choice, ["其他微短剧", "重点/普通微短剧", "重点微短剧", "普通微短剧"]),
        new(Permit, "备案号 / 发行许可证号", Evidence),
        new(QualificationFile, QualificationFile, Evidence, WeixinSeriesFieldKind.Attachments),
        new(Production, Production, Evidence, WeixinSeriesFieldKind.Attachments),
        new(Authorization, "版权采买与播出授权证明", Evidence, WeixinSeriesFieldKind.Attachments),
        new(AiDeclared, "AI 内容声明", Evidence, WeixinSeriesFieldKind.Choice, ["是", "否"]),
        new(AiProduction, "AI 制作证明", Evidence, WeixinSeriesFieldKind.Attachments),
        new(AiOwnershipMode, AiOwnershipMode, Evidence, WeixinSeriesFieldKind.Choice, ["启用", "关闭"]),
        new(AiOwnership, "AI 软件权属证明文件", Evidence, WeixinSeriesFieldKind.Attachments),
        new(Actors, "演员资料（每行 姓名 | 角色 | 简介）", Evidence, WeixinSeriesFieldKind.Multiline),
        new(ActorPhotos, ActorPhotos, Evidence, WeixinSeriesFieldKind.Attachments),
        new(EpisodeMode, EpisodeMode, Videos, WeixinSeriesFieldKind.Choice, ["全部", "连续区间", "指定集数"]),
        new(EpisodeStart, EpisodeStart, Videos),
        new(EpisodeEnd, EpisodeEnd, Videos),
        new(EpisodeIndexes, "指定集数（例如 1,3,5-8）", Videos),
    ];
}
