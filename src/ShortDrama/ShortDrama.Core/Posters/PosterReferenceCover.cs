namespace ShortDrama.Core.Posters;

public static class PosterReferenceCover
{
    public const string Mode = "reference_regenerate";
    public const double MinimumChangedArea = 0.40;

    public const string DefaultPrompt = """
        仅用原海报确认主角身份、面部特征、人物关系和题材氛围。围绕同一剧情设计一张新封面。
        采用与原图不同的单一场景，同时改变镜头景别、主要人物的站位和动作。围绕新的互动安排前景、背景和光源，让有效画面至少有 40% 发生结构变化。
        放弃原图的分屏、拼贴、装饰分隔、标志性道具摆位和人物剪影组合。避免用裁切、放大、调色、翻转或仅替换文字来仿制原图。
        保持人物比例自然，避免重复人物、变形、边框或画中画。
        清除原图中的旧剧名、字幕、宣传语、角标和其他文字。成品只写一次“{title}”，完整、清晰、逐字准确。
        标题放在画面下部并避开人物脸部和画面边缘。按请求尺寸构图，给主体和标题留出安全区。
        """;

    public const string AlternateComposition = """
        这次重新从原图建立画面。选择原图未出现的地点与互动，用中近景侧面或对角线构图，重新安排主角的前后距离、动作和视线。
        让背景环境和主体轮廓都发生变化，完全弃用原图的场景划分和站位。仍须保持主角可辨认，只保留指定新剧名。
        """;
}
