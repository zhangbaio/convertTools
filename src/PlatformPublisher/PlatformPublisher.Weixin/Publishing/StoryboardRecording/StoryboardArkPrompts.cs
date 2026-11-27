namespace PlatformPublisher.Weixin.Publishing;

internal static class StoryboardArkPrompts
{
    internal const string Analysis = """
    你会收到按时间排序的本集真实抽帧。把每张图独立反推成可复现画面的中文分镜提示词。
    每条约40至75字，描述可见的主体、动作、景别、机位、构图、环境和光线。
    仅凭静帧无法验证的运镜、对白和声音不要补写。不要把字幕、台标当作剧情元素。
    从这些图中各选一个最清晰的主要人物、场景、道具，写简短名称和便于出图的外观描述。
    reference_shot 为 1 起算的镜号，必须是能看清该资产的一张输入图。
    缺少可确认的资产时将对应字段填 null，保留字段，不编造人物或物品。
    只输出 JSON 对象，shots 必须逐一覆盖提供的镜号，结构如下：
    {"shots":[{"shot":1,"prompt":"分镜提示词"}],"assets":{
    "character":{"name":"名称","description":"外观","reference_shot":1},
    "scene":{"name":"名称","description":"外观","reference_shot":1},
    "prop":{"name":"名称","description":"外观","reference_shot":1}}}
    """;

    internal const string Prop = """
    这是同一集视频的多张候选帧。只识别一个对剧情有用且画面中可确认的独立道具，并选它最清楚的一帧。
    优先选择人物拿着、使用、递交或抱着的物品。若多个道具有交互，优先选居前景、轮廓清楚、占画面较大且遮挡较少的一个。
    不要选择普通背景、草木、泥土、服装、建筑及固定构件。必要时选没有字幕遮挡的画面。
    name 写具体物品名称，description 只描述该帧看得到的形状、颜色和材质特征，避免补写看不见的细节。
    reference_frame 是上文候选帧的编号，visibility 是 clear 或 partial（物品可辨认但有截断、明显遮挡或偏小）。
    没有可确认的独立道具时返回 {"prop": null}。
    只输出 JSON：{"prop":{"name":"物品名称","description":"可见外观","reference_frame":1,"visibility":"clear"}}
    """;

    internal static readonly (string Kind, string Label, string Size, string Instruction)[] Assets =
    [
        ("character", "人物四视图", "3136x1344", "同一人物的白底角色设定图，横排恰好四格，从左到右严格依次排列：① 正脸头肩特写（直视镜头，画面内只露头肩）；② 正面全身；③ 侧面全身（90度正侧面）；④ 背面全身。四格的五官、体态、发型与服装保持一致，后三格从头顶到脚底均完整入画。四格彼此分开，统一纯白背景和均匀光线。参考图中如果有多人，只保留描述所指的主要人物。"),
        ("scene", "场景资产图", "2816x1584", "空镜场景设计参考图，保持构图、空间结构、材质和主光，去掉画面中的人物。"),
        ("prop", "道具资产图", "2048x2048",
            "单件道具的独立商品照，画面只能出现指定物件，放在纯浅灰背景上居中展示。" +
            "彻底去除参考图里的人脸、身体、手、衣物、环境和其他物件。即使道具在人物怀里或手里，也必须取出单独展示。" +
            "保留物件可见的形状、颜色、花纹和材质，补全被遮挡的物件边缘。" +
            "若为证卡或文件类道具，只呈现空白外观，不包含个人数据、印章、编号或正文。"),
    ];
}
