namespace TikTokPublisher.Core.Models;

public static class ClientSettingsDefaults
{
    public const string AiTextEndpoint = "https://ark.cn-beijing.volces.com/api/v3";
    public const string AiTextModel = "doubao-seed-2-0-lite-260215";
    public const int AiTextTimeoutSeconds = 120;
    public const int AiTextMaxBatchSize = 10;
    public const string TiktokRoleReferenceSelectionMode = "local";
    public const bool TiktokRoleReferenceAiFallbackEnabled = true;
    public const string TiktokRoleVectorViewMode = "multi_angle";
    public const string PosterMode = "original";
    public const string ImageProvider = "doubao";
    public const string ImageModelEndpoint = "https://ark.cn-beijing.volces.com/api/v3";
    public const string ImageModelId = "doubao-seedream-5-0-lite-260128";
    public const string DoubaoImageResolution = "2K";
    public const string DoubaoImageRatio = "3:4";
    public const string OfoxImage2ModelId = "openai/gpt-image-2";
    public const string OfoxImage2Endpoint = "https://api.ofox.ai/v1";
    public const string OfoxImage2Quality = "medium";
    public const string OfoxImage2Size = "auto";
    public const bool PosterTitleVerifyEnabled = true;
    public const string PosterTitleVerifyMode = "fallback_repaint";
    public const int PosterTitleVerifyAiRetryCount = 1;
    public const int FrameExtractEpisodeIndex = 1;
    public const double FrameExtractTime = 5.0;
    public const string FrameExtractNeighborOffsetsSeconds = "2,4";
    public const string FrameExtractFallbackPercents = "10,25,50,75";
    public const bool TiktokAllowOverLimitUploadImport = true;
    public const int TiktokOverLimitDownloadEpisodeCount = 120;
    public const string TiktokProjectImageGenerationMode = "image_template";
    public const string TiktokProjectImageTemplateId = "image-template-project-image-3";
    public const string TiktokProjectImageTemplateName = "图片模板工程图3";
    public const int TiktokProjectImageCount = 4;
    public const int TiktokProjectImageRenderEpisodeLimit = 6;
    public const string TiktokProjectImageSubtitleAiMode = "fast";
    public const string TiktokProjectImageFableCutRoot = "";
    public const int TiktokProjectImageFableCutClipCount = 24;
    public const string TiktokProjectImageFableCutScreenshotStyle = "standard";
    // Empty means use the DOCX embedded in TikTokPublisher.Core.
    public const string TiktokProofTemplateDocxPath = "";
    public const string TiktokProofWpsPath = "";
    public const string TiktokProofPdfRenderer = "wps";
    public const bool TiktokProofKeepDocx = false;

    public const string FeishuCommandHelpText = """
        【飞书 TikTok 上传命令教程】

        群聊规则：默认必须先 @机器人，再写命令；普通群消息不会识别。
        私聊规则：启用私聊后，可直接发送命令，也支持 @机器人 命令。
        连接规则：一个飞书机器人建议只连接一台电脑，避免多设备同时处理同一条命令。

        一、上传 TikTok 剧集
        @机器人 上传剧集 剧名A
        @机器人 上传TikTok
        剧名A
        剧名B

        可选参数：
        工作目录: E:\tiktok
        账号: 默认
        账号: 账号A,账号B
        账号: 全部
        步骤: download,rewrite_info,generate_poster,generate_project_images,generate_proof_material,upload_series
        自动执行: 是

        多账号说明：多账号执行时使用每个账号「基础设置」中保存的工作目录。

        二、队列与状态
        @机器人 执行队列
        @机器人 执行队列
        账号: 全部
        @机器人 停止队列
        @机器人 状态

        三、教程命令
        @机器人 教程 / 帮助 / help：回复文本教程
        @机器人 菜单 / 卡片教程：回复按钮卡片教程
        """;

    public const string AiTagSystemPrompt = "";

    public const string AiTagBatchPrompt = "";

    public const string AiFullInfoSystemPrompt =
        "你是短剧宣发改写助手。请根据提供的短剧项目信息，一次性生成适合视频号宣传的完整文案字段。" +
        "新剧名必须使用标准、自然、通顺的中文标题表达，且字数严格控制在 6-15 个汉字。" +
        "如果生成结果不满足 6-15 个汉字，必须在输出前自行重写，直到满足要求。" +
        "新剧名、推荐语都必须符合正常宣发尺度，不得包含违背伦理道德、炫富拜金、宣扬极端复仇、色情低俗、羞辱挑衅、擦边暗示等不良价值导向。" +
        "整体表达要通顺、克制、正式，不要过度口语化，不要使用网感黑话和标题党措辞。" +
        "只输出 JSON，不要解释，不要 Markdown。\n" +
        "海报标题安全规则：new_title 会用于 AI 海报改字和快手封面审核，请优先使用常见、标准、字形清晰、图像模型不易误写的简体中文汉字。" +
        "避免使用生僻字、繁体字、异体字、复杂偏旁字，以及容易和近形字混淆的字。" +
        "如能表达同样含义，请避开这些易错字或近形组合：冤/宛/苑、赢/嬴、赘/整、娇/骄、婿/胥、虐/虎、孽/蘖、阔/闯、曦/羲、璟/景、衍/街、羁/羂、魇/厌、霁/齐、漾/羡。" +
        "如果候选 new_title 命中这些字，请主动换成更常见、更稳妥的同义表达。";

    public const string AiFullInfoBatchPrompt = """
        请根据下面的多个短剧项目信息，逐个生成：
        1. new_title：适合短剧传播的新剧名，必须严格为 6-15 个汉字，必须与 original_title 明显不同，不能只是加“热播版/新篇/完整版/高能版”等尾巴。
        2. tagline：推荐语，8-20 个字，强调冲突、关系或情绪钩子。
        3. synopsis：简介，默认 40-220 个字；如果输入包含 target_synopsis_length，字数尽量接近该数字，可上下浮动约 25%，并交代主要人物关系、核心冲突和情绪卖点。
        4. new_title、tagline 都不得出现违背伦理道德、炫富拜金、宣扬极端复仇、色情低俗、过度口语化等不良价值导向。
        5. 文案不要使用“杀疯了、往死里整、陪睡、炫富打脸、赢麻了、家人们谁懂”等风格化措辞。
        6. 输出前必须自行检查：new_title 只能包含 6-15 个汉字，不能多一个字，也不能少一个字；若不满足必须重写，不允许把不合规结果输出。

        输出要求：
        1. 只输出 JSON。
        2. JSON 格式固定为：
        {"items":[{"id":"1","new_title":"...","tagline":"...","synopsis":"..."}]}
        3. 每个输入项目都必须返回一条 items 记录。
        4. 不要遗漏 id。

        输入项目：
        {items_json}

        差异化改写补充要求：
        1. 如果输入包含 forbidden_titles，new_title 不得与其中任何标题重复或高度相似。
        2. 如果输入包含 forbidden_synopses，synopsis 不得照抄或高度复用这些简介，要换叙事角度、句式和卖点。
        3. 如果输入包含 target_synopsis_length，synopsis 字数尽量接近该数字，允许上下浮动约 25%。
        4. 如果输入包含 rewrite_variant_key，把它当作本次改写的差异化种子；同一 original_title 多次改写时，必须尽量给出不同表达。
        海报标题安全规则：
        1. new_title 会被用于 AI 海报改字和快手封面审核，必须优先选择常见、标准、字形清晰、图像模型不易误写的简体中文汉字。
        2. 避免使用生僻字、繁体字、异体字、复杂偏旁字，以及容易和近形字混淆的字。
        3. 如能表达同样含义，请避开这些易错字或近形组合：冤/宛/苑、赢/嬴、赘/整、娇/骄、婿/胥、虐/虎、孽/蘖、阔/闯、曦/羲、璟/景、衍/街、羁/羂、魇/厌、霁/齐、漾/羡。
        4. 如果候选 new_title 命中上述字，请主动换成更常见、更稳妥的同义表达。
        """;

    public const string AiFullInfoRetryPrompt = """
        上一次改写结果不合格，请重新生成，并严格遵守：
        1. new_title 必须严格为 6-15 个汉字，并且和 original_title 明显不同，不能只是加“热播版/新篇/高能版”等后缀。
        2. tagline 不能为空，且要有宣传钩子感。
        3. synopsis 不能为空，且要包含核心冲突。
        4. 不要生成 tags/标签 字段。
        5. 新剧名、推荐语必须价值导向正常，不得包含炫富拜金、极端复仇、低俗擦边或过度口语化表达。
        6. 输出前必须再次自检 new_title 的字数；只要不在 6-15 个汉字范围内，就继续改写，不允许输出不合规结果。

        输入项目：
        {items_json}

        重试补充要求：
        1. 必须修复校验失败字段，尤其 synopsis 要接近 target_synopsis_length，并避开 forbidden_synopses。
        2. 不要只替换几个词，要重组句式、叙事重点和情绪卖点。
        海报标题安全规则：
        1. new_title 会被用于 AI 海报改字和快手封面审核，必须优先选择常见、标准、字形清晰、图像模型不易误写的简体中文汉字。
        2. 避免使用生僻字、繁体字、异体字、复杂偏旁字，以及容易和近形字混淆的字。
        3. 如能表达同样含义，请避开这些易错字或近形组合：冤/宛/苑、赢/嬴、赘/整、娇/骄、婿/胥、虐/虎、孽/蘖、阔/闯、曦/羲、璟/景、衍/街、羁/羂、魇/厌、霁/齐、漾/羡。
        4. 如果候选 new_title 命中上述字，请主动换成更常见、更稳妥的同义表达。
        """;

    public const string LegacyPosterLayoutDetectPrompt =
        "你是短剧海报版式分析助手。请识别海报上“现有主标题文字”的最小覆盖区域，并返回 JSON。" +
        "要求只返回 JSON，坐标和尺寸都用 0 到 1 的比例。" +
        "返回的标题区域需要适合放置标准、清晰、审核友好的中文印刷体标题。";

    public const string PosterLayoutDetectPrompt =
        "你是短剧海报版式分析助手。请识别海报上“所有现有剧名/标题相关文字”的整体最小外接矩形，并返回 JSON。" +
        "这些文字包括主标题、副标题、季数标记（如“第三季”“第X季”），以及与剧名同属一组的宣传短句；" +
        "凡是会随剧名替换而需要一并去掉的旧文字行，都要纳入同一个矩形，不要只框主标题、漏掉季数或副标题。" +
        "要求只返回 JSON，坐标和尺寸都用 0 到 1 的比例；矩形要刚好覆盖上述全部标题文字行、尽量贴合，不要框进无关画面。" +
        "返回的区域需要适合放置标准、清晰、审核友好的中文印刷体标题。";

    public const string LegacyPosterInpaintPrompt =
        "这是海报局部改字任务，不是重绘海报。" +
        "只允许在遮罩区域内把原有剧名文字替换为“{title}”，图片其余任何内容都不能修改。" +
        "新标题必须优先使用标准简体中文无衬线印刷粗体，接近黑体、微软雅黑或思源黑体。" +
        "逐字准确，禁止异体字、相似错字、装饰字、手写体和变形字。" +
        "允许轻微描边，但不得为了海报设计感改变字形。宁可使用普通标准粗黑体，也不要生成花哨但不规范的标题字。\n" +
        "标题必须优先使用标准简体中文无衬线印刷粗体，逐字准确。\n" +
        "禁止异体字、相似错字、繁体字、艺术字、手写体、装饰字和变形字。\n" +
        "高风险字如“继、媳、鬓、馨、骤、瓷、赢、寡、赘”等必须使用常见、标准、易识别的简体印刷字写法。";

    public const string PosterInpaintPrompt = """
        这是海报文字清理与改标题任务，不是重绘人物或背景。
        删除输入海报中除目标新剧名外的所有可见文字，包括旧主标题、副标题、季数、宣传语、人物或角色姓名、演员名、作者、改编及来源说明、版权或出品信息、字幕、水印、Logo文字和角标。
        用周围背景自然补全被删除的文字区域；人物、脸部、服装、道具、背景、构图、尺寸、比例、光影和清晰度必须保持不变。
        然后只添加一次目标新剧名“{title}”。最终成品中唯一允许出现的可读文字就是“{title}”，不得保留或新增任何其他中文、英文、拼音、数字或符号文字。
        新标题必须使用标准、清晰、易识别的简体中文印刷粗体，逐字准确；禁止繁体字、异体字、错别字、手写体、花体和变形字。
        """;

    public const string LegacyPosterInpaintSafeRetryPrompt =
        "这是安全合规的局部改字任务。只允许在遮罩区域内将现有标题替换为“{title}”，不能修改遮罩区域外的任何像素。" +
        "请完全放弃海报设计字风格，直接使用最普通、最标准、最像黑体/微软雅黑的中文印刷粗体。" +
        "即使风格更朴素也可以，必须优先保证每个汉字都是标准印刷体、笔画完整、逐字正确。\n" +
        "标题必须优先使用标准简体中文无衬线印刷粗体，逐字准确。\n" +
        "禁止异体字、相似错字、繁体字、艺术字、手写体、装饰字和变形字。\n" +
        "允许轻微描边，但不得为了海报设计感改变字形。\n" +
        "宁可使用普通标准粗黑体，也不要生成花哨但不规范的标题字。\n" +
        "高风险字如“继、媳、鬓、馨、骤、瓷、赢、寡、赘”等必须使用常见、标准、易识别的简体印刷字写法。";

    public const string PosterInpaintSafeRetryPrompt = """
        这是安全合规的海报文字清理任务。保持人物、背景、服装、道具、构图、尺寸和光影不变。
        清除原图中所有旧文字和小字，包括人物名、演员名、作者及改编来源、宣传语、季数、字幕、水印、Logo文字和角标；用背景自然补全。
        最后只写一次目标新剧名“{title}”。最终成品只能包含这个目标剧名，不得出现任何其他可读文字。
        目标剧名必须使用最普通、标准、清晰的简体中文印刷粗体，逐字准确，不得使用繁体字、异体字、艺术字或变形字。
        """;

    public const string LegacyPosterGenerationPrompt = """
        参考输入海报图，执行一次精确的海报改字编辑。只把海报中现有的剧名文字替换为"{title}"，其余所有内容必须保持不变。
        字形规范（最高优先级）：
        1. 标题必须使用 GB2312/GB18030 标准简体中文印刷字，逐字准确。
        2. 禁止异体字、繁体字、艺术变形字、手写体、装饰字。
        3. 宁用普通标准黑体，不用花哨但字形错误的字。

        高风险字强制规范写法（必须严格执行）：
        1. “继”：左部为“纟”（三点绞丝旁），右部为“㐄+小”，禁止写成繁体“繼”或异体写法。
        2. “媳”：左部“女”旁，右部“息”（自+心），笔画不可增减。
        3. 其他高风险字：鬓、馨、骤、瓷、赢、寡、赘，均使用新华字典标准简体印刷形。

        排版约束：
        1. 优先使用思源黑体/方正黑体风格的无衬线粗体。
        2. 允许轻微白色描边，但绝不因设计感改变字形结构。
        3. 字符间距、大小与原海报保持一致。

        核验步骤（生成前自查）：
        生成前逐字对照标准简体字形，确认每个笔画与 GB 标准一致，不符合则重新生成。
        标题必须优先使用标准简体中文无衬线印刷粗体，逐字准确。
        禁止异体字、相似错字、繁体字、艺术字、手写体、装饰字和变形字。
        允许轻微描边，但不得为了海报设计感改变字形。
        宁可使用普通标准粗黑体，也不要生成花哨但不规范的标题字。
        高风险字如“继、媳、鬓、馨、骤、瓷、赢、寡、赘”等必须使用常见、标准、易识别的简体印刷字写法。
        """;

    public const string PosterGenerationPrompt = """
        参考输入海报执行精确的文字清理和新标题生成，保持原海报的人物、脸部、服装、动作、道具、背景、构图、尺寸、比例、颜色、光影和清晰度不变。

        文字清理（最高优先级）：
        1. 删除原图中所有可见文字，包括旧主标题、副标题、季数、宣传语、人物或角色姓名、演员名、作者、改编及来源说明、版权或出品信息、字幕、水印、Logo文字和角标。
        2. 用周围背景自然补全所有被删除文字的区域，不得留下文字残影、描边、底纹或模糊字。
        3. 清理完成后只添加一次目标新剧名“{title}”。最终成品唯一允许出现的可读文字就是“{title}”；不得保留或新增任何其他中文、英文、拼音、数字或符号文字。

        标题规范：
        1. 目标标题必须逐字准确，使用标准、清晰、易识别的简体中文印刷粗体。
        2. 禁止繁体字、异体字、错别字、手写体、书法体、花体、艺术变形字和残缺笔画。
        3. 标题位置沿用原主标题区域，可轻微描边，但不得为了设计感牺牲字形正确性。
        """;

    public const string LegacyPosterGenerationSafeRetryPrompt =
        "参考输入海报，执行一次安全合规的局部标题替换。只把现有主标题替换为“{title}”，其余画面保持不变。" +
        "请把标题理解为普通标准印刷体标题，不要生成书法感、设计感、笔刷感、夸张描边感导致的异常字形。" +
        "可以牺牲海报设计感，但不能牺牲任何一个汉字的标准字形和可识别性。\n" +
        "标题必须优先使用标准简体中文无衬线印刷粗体，逐字准确。\n" +
        "禁止异体字、相似错字、繁体字、艺术字、手写体、装饰字和变形字。\n" +
        "允许轻微描边，但不得为了海报设计感改变字形。\n" +
        "宁可使用普通标准粗黑体，也不要生成花哨但不规范的标题字。\n" +
        "高风险字如“继、媳、鬓、馨、骤、瓷、赢、寡、赘”等必须使用常见、标准、易识别的简体印刷字写法。";

    public const string PosterGenerationSafeRetryPrompt = """
        参考输入海报执行安全合规的文字清理。人物、背景、服装、道具、构图、尺寸、比例和光影必须保持不变。
        删除原图中除目标新剧名外的全部文字和小字，包括人物名、演员名、作者、改编来源、宣传语、季数、字幕、水印、Logo文字和角标，并用背景自然补全。
        最后只写一次“{title}”。最终成品只能出现这个目标剧名，不得出现其他中文、英文、拼音、数字或文字残影。
        目标剧名必须使用普通、标准、清晰的简体中文印刷粗体，逐字正确，不得使用繁体字、异体字、艺术字或变形字。
        """;

    public const string PosterNameSystemPrompt =
        "你是短剧海报名助手。请输出一个适合作为海报标题的中文短句。" +
        "输出必须使用常见、标准、易识别的简体中文汉字，避免生僻字、异体字和容易写错的字。" +
        "不要带扩展名、不要解释。\n" +
        "优先使用常见、标准、易识别的简体中文汉字，避免生僻字、异体字、繁体字和容易误写的字。";

    public const string PosterNameUserPrompt = """
        请为这个短剧生成 1 个适合作为海报标题的中文短句。
        要求：
        1. 8 到 18 个汉字。
        2. 风格偏短剧宣发，有钩子感。
        3. 不要输出“海报”“短剧”“jpg”“png”等字样。
        4. 不要带标点、引号、解释。
        5. 只输出标题本身。
        6. 优先使用常见、标准、易识别的简体中文汉字，避免生僻字、异体字、繁体字和容易误写的字。

        短剧标题：{project_title}
        原剧名：{original_title}
        推荐语：{tagline}
        简介：{synopsis}
        """;

    public const string LegacyFrameCoverPrompt =
        "请完成以下两个任务：\n" +
        "1. 删除图片中所有文字（包括字幕、水印、标题、角标等），用周围背景自然填充被删除区域。\n" +
        "2. 在图片下半部分主标题区添加新标题文字：{title}。标题成品只能包含剧名本身，不要添加方括号、书名号、引号或其他包裹符号。标题必须足够大，整体占据主标题安全区的大部分宽度，优先使用两行大标题排版，不要缩成一行小字，也不要放成角落小标题。\n" +
        "3. 标题风格要更像影视短剧封面主标题，允许轻微错落、行长变化和大小对比，整体要有张力和设计感，但不要做成过硬、过方、过于规整的办公黑体大字。\n" +
        "4. 字形要标准、清晰、易读，属于规范的简体中文海报粗标题；可以有轻微圆润感、收笔变化和自然节奏，但不要使用书法体、手写体、花体、卡通体和难认的设计字。\n" +
        "5. 标题颜色优先暖白、浅金白或柔和金色，少用高饱和亮黄；描边不要过粗，使用深色中等描边配柔和阴影，避免生硬发黑的厚重边框。\n" +
        "6. 比例与构图规则：保持人物、脸部、身体、道具和文字的真实比例，严禁横向拉伸、纵向压缩、头身比例变形、脸部变宽或文字被挤扁。\n" +
        "7. 如果用于横屏封面或宽屏封面，必须重新组织自然横屏构图，允许合理扩展左右两侧背景、补充环境和光影；不要把竖屏海报压扁、拉宽或简单缩放到横屏，不要添加黑边、白边、相框、截图边框或竖屏海报画中画效果。\n" +
        "不要裁剪、缩放或改变图片尺寸和比例，不要修改人物、服装、背景和构图。";

    public const string FrameCoverPrompt = LegacyFrameCoverPrompt +
        "\n最终文字规则：除目标新剧名“{title}”外，不得保留人物或角色姓名、演员名、作者、改编或来源说明、版权或出品信息、宣传语、副标题、季数、字幕、水印、Logo文字、角标以及任何其他可读文字。成品中唯一允许出现的文字就是目标新剧名。";
}
