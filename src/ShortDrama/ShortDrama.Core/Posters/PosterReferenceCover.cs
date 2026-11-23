namespace ShortDrama.Core.Posters;

public static class PosterReferenceCover
{
    public const string Mode = "reference_regenerate";

    public const string DefaultPrompt = """
        以输入的原始短剧海报为参考，重新构图并生成一张可直接发布的新封面。
        保留主要人物的身份、面部特征、人物关系和题材氛围，服饰、动作和表情可以延续原图。
        重新设计景别、主体位置、场景和光影，让新封面有明显不同的画面。保持人物比例自然，避免重复人物、变形、边框或画中画。
        清除原图中的旧剧名、字幕、宣传语、角标和其他文字。成品只写一次“{title}”，完整、清晰、逐字准确。
        标题与新画面一起设计，避开人脸和画面边缘。按请求尺寸构图，给主体和标题留出安全区。
        """;
}
