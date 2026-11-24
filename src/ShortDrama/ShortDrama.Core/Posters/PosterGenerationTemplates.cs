namespace ShortDrama.Core.Posters;

public static class PosterGenerationTemplates
{
    public const string DirectMode = "original";
    public const string EraseMode = "erase_fixed_lower_title";

    public const string PlainTextDirectPrompt = """
        根据输入海报识别短剧人物和题材，独立创作一张可直接发布到 TikTok 的成品海报。
        保留主要人物的身份、核心面部特征和人物关系，选用符合剧情的新场景。重新设计构图、镜头视角、人物站位和画面布局。
        为人物安排新的动作、服装和表情，重做光影与主色调。通过新场景和新画面完成整体创作，避免局部换字或修补式改图。
        清除原图的所有文字、字幕、标志和水印。按任务提供的新剧名排版，成品只出现一次该剧名。保持原有拼写、大小写、空格和标点，可自然换行。
        标题清晰易读，避开人物面部和画面边缘。保持人物比例自然、五官清晰，呈现主体突出的电影海报质感。
        只输出一张成品，画面避免乱码、额外文字、重复人物和畸形肢体。
        """;

    public static bool SupportsPlainText(string? mode) => mode is DirectMode or EraseMode;

    public static string ResolvePlainText(string mode, string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? mode == EraseMode ? ErasePrompt : PlainTextDirectPrompt : prompt;

    public static string ComposePlainText(string mode, string? prompt, string title)
    {
        var body = ResolvePlainText(mode, prompt);
        return mode == DirectMode ? $"{body}\n\n本次海报剧名为《{title}》。" : body;
    }

    public const string DirectPrompt = """
        参考原海报和短剧题材，制作一张可发布的新海报。保留主要人物的身份、面部特征和人物关系，更换背景与构图，调整人物站位、动作、服饰和表情，让新场景符合剧情。
        清除原剧名、字幕、标识、水印和其他文字。成品只出现一次“{title}”，保留原有拼写、大小写、空格和标点，允许自然换行。标题醒目、易读，避开人脸和画面边缘。
        按 {ratio} 构图，保持人物比例自然，避免重复人物、畸形、乱码和边框。
        """;

    public const string ErasePrompt = """
        将原海报处理成一张无字底图。清除原剧名、字幕、宣传语、角标、标识和水印，用周围的背景、纹理和光影自然补全文字区域。
        保留原图的人物、表情、服饰、动作、场景、构图和比例。交付的底图不得出现文字、字母、数字或仿文字符号。新剧名由程序在出图后写入。
        """;

    public const string ReferencePrompt = """
        从原海报提取主要人物的身份、面部特征、关系和题材，围绕同一剧情设计一张新封面。采用新的单一场景，改变镜头景别、人物站位和互动，重新安排前后景与光线。
        有效画面至少有 40% 的结构变化。避免沿用原图的分屏、拼贴、人物站位和标志性道具摆放。裁切、缩放、调色或换字不计入画面差异。
        成品只出现一次“{title}”，保持拼写和标点准确，标题清晰并避开人脸。按 {ratio} 构图，只输出一张成品。
        """;

    public const string TitleVerificationPrompt = """
        阅读整张海报，检查“{title}”是否准确出现一次，保留实际看到的拼写、大小写、标点和词间空格。允许正常换行。检查是否还有其他可读文字，无法看清时说明原因。只报告检查结果。
        只返回一个 JSON 对象：{"detectedTitle":"","matchesTarget":true,"hasResidualText":false,"residualText":"","reason":""}
        """;

    public static bool IsSupportedMode(string? mode) => mode is DirectMode or EraseMode or PosterReferenceCover.Mode;

    public static string ForMode(string mode) => mode switch
    {
        DirectMode => DirectPrompt,
        EraseMode => ErasePrompt,
        PosterReferenceCover.Mode => ReferencePrompt,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "海报模式没有可见的生成模板。"),
    };
}
