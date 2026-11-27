namespace TikTokPublisher.Core.Models;

public static class ClientSettingsDefaults
{
    public const string AiTextEndpoint = "https://ark.cn-beijing.volces.com/api/v3";
    public const string AiTextModel = "doubao-seed-2-1-lite-260915";
    public const string RetiredAiTextModel = "doubao-seed-2-0-lite-260215";
    public const int AiTextTimeoutSeconds = 120;
    public const int AiTextMaxBatchSize = 10;
    public const string AiNewTitleRandomness = "medium";
    public const string AiNewTitleRandomnessLow = "low";
    public const string AiNewTitleRandomnessHigh = "high";

    public static string NormalizeAiNewTitleRandomness(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Equals(AiNewTitleRandomnessLow, StringComparison.OrdinalIgnoreCase) || text == "低")
            return AiNewTitleRandomnessLow;
        if (text.Equals(AiNewTitleRandomnessHigh, StringComparison.OrdinalIgnoreCase) || text == "高")
            return AiNewTitleRandomnessHigh;
        return AiNewTitleRandomness;
    }

    public static double AiNewTitleTemperature(string? value) =>
        NormalizeAiNewTitleRandomness(value) switch
        {
            AiNewTitleRandomnessLow => 0.3,
            AiNewTitleRandomnessHigh => 1.1,
            _ => 0.75,
        };
    public const int XingePollIntervalSeconds = 30;

    // 原先默认 3 秒。读配置时把这个旧默认值抬到 30 秒，手填的其它间隔保持不变。
    public static int NormalizeXingePollIntervalSeconds(int seconds)
    {
        if (seconds <= 0 || seconds == 3)
            seconds = XingePollIntervalSeconds;
        return Math.Clamp(seconds, 1, 60);
    }

    public const string TiktokRoleReferenceSelectionMode = "local";
    public const bool TiktokRoleReferenceAiFallbackEnabled = true;
    public const string TiktokRoleVectorViewMode = "multi_angle";
    public const string PosterMode = "original";
    public const string PosterLayoutRequestMode = "standard";
    public const string PosterLayoutRequestModeFast = "fast";
    public const string ImageProvider = "doubao";
    public const string ImageModelEndpoint = "https://ark.cn-beijing.volces.com/api/v3";
    public const string ImageModelId = "doubao-seedream-5-0-flash-260915";
    public const string ImageModelIdPro = "doubao-seedream-5-0-pro-260628";

    public static string NormalizeImageModelId(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Equals(ImageModelIdPro, StringComparison.OrdinalIgnoreCase))
            return ImageModelIdPro;
        return ImageModelId;
    }

    public static bool AllowsGroupGenerationParameter(string? modelId)
    {
        var model = (modelId ?? "").Trim();
        return model.Contains("seedream-5-0-lite", StringComparison.OrdinalIgnoreCase)
            || model.Contains("seedream-5-0-260128", StringComparison.OrdinalIgnoreCase)
            || model.Contains("seedream-4-5", StringComparison.OrdinalIgnoreCase)
            || model.Contains("seedream-4-0", StringComparison.OrdinalIgnoreCase);
    }
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
    public const bool TiktokAccountAnalyticsEnabled = false;
    public const bool TiktokAllowOverLimitUploadImport = true;
    public const int TiktokMinimumEpisodeCount = 30;
    public const int TiktokMaximumEpisodeCount = 120;
    public const int TiktokOverLimitDownloadEpisodeCount = TiktokMaximumEpisodeCount;
    public const string TiktokProjectImageTemplateId = "image-template-project-image-10";
    public const string TiktokProjectImageTemplateName = "图片模板工程图10";
    public const int TiktokProjectImageCount = 4;
    public const int TiktokProjectImageRenderEpisodeLimit = 6;
    public const string TiktokProjectImageSubtitleAiMode = "fast";
    // Empty means use the DOCX embedded in TikTokPublisher.Core.
    public const string TiktokDefaultPublishMode = "auto_after_review";
    public const bool TiktokDefaultConsignmentEnabled = true;
    public const bool TiktokDefaultAnchorPromotionEnabled = true;
    public const string TiktokProofTemplateDocxPath = "";
    public const string TiktokProofTemplateLanguage = "zh";
    public const string TiktokProofWpsPath = "";
    public const string TiktokProofPdfRenderer = "wps";
    public const bool TiktokProofKeepDocx = false;

    public const string RemoteCommandHelpText = """
        【TikTok 远程上传命令教程】


        一、上传 TikTok 剧集
        上传剧集 剧名A
        上传TikTok
        剧名A
        剧名B

        可选参数：
        工作目录: E:\tiktok
        账号: 默认
        账号: 账号A,账号B
        账号: 全部
        步骤: download,rewrite_info,generate_poster,generate_project_images,generate_proof_material,upload_series
        自动执行: 是

        多账号说明：多账号执行时使用每个账号「发布配置 → 工作目录」中保存的工作目录。

        二、队列与状态
        执行队列
        执行队列
        账号: 全部
        停止队列
        状态

        三、教程命令
        教程 / 帮助 / help：回复文本教程
        菜单 / 卡片教程：回复按钮卡片教程
        """;

    public const string TikTokUnifiedPublishInfoPrompt = """
        你负责生成 TikTok 短剧发布文案。以原剧资料为依据，用自然、精炼的表达突出剧情中已有的核心冲突、关系变化或关键反转，保留原意和事实。只交付一版结果。
        【语言】按 output_language 指定的语言代码输出。为 auto 时，根据 original_title 和 synopsis 判断原文语言。中文用简体中文，英文用英文。支持 zh、en、id、pt、ja、th、es、ko、tr、hi，language 填实际使用的代码。无法判断或不支持时，language 返回 unsupported，其他字符串字段返回空字符串，themes 返回空数组。
        【新剧名】改写 original_title，让新剧名鲜明、易读，与原剧名明显不同。中文剧名 6–15 个字，英文及其他语种 8–80 个字符。避开 forbidden_titles 中的相同或近似剧名，以及 occupied_exact_titles 中的同名剧名。避免装饰符号和夸张宣传。
        【简介轻改写】rewrite_synopsis 为 false 时，description 返回空字符串。为 true 时，重组 synopsis 的句式，保留角色姓名、人物关系、地点、数字、动机、事件顺序、因果、转折和结局。可以拆句或合句，禁止增加剧情、揣测、宣传语或演员姓名。
        简介目标长度接近原文，允许增减 10%，下限向上取整，上限向下取整。中文、日语和泰语按字计数，计入文字和数字，空格和标点不计入。英文及其他语种按词计数，词内连字符和撇号不断词。中文简介最多 300 个字符，其他语种最多 900 个字符。
        禁止复用完整原句，单改大小写或标点也算复用。按词计数时，与原文相同的连续 5 词片段最多占候选简介全部此类片段的 25%。避开 forbidden_synopses 中的历史简介。
        【题材与观众】从 theme_options 选出 theme_count 个最贴合剧情的题材，排除 existing_themes，禁止重复或自定义。theme_count 为 0 时返回空数组。ask_audience 为 true 时，audience 只填「男」或「女」。否则返回空字符串。
        只返回一个 JSON 对象，禁止附加说明。
        {"title":"","description":"","themes":[],"audience":"","language":""}
        """;

    // Retained for the shared publish-info workflow and for recognizing saved TikTok defaults.
    public const string AiPublishInfoPrompt = """
        你是 TikTok 短剧发布信息助手。根据输入的短剧信息，一次生成发布所需的全部字段。

        title（新剧名）：
        1. 在不改变原意的前提下，把 original_title 改写成更有冲击力的新剧名，必须与原剧名明显不同，不能只加“热播版/新篇/完整版/高能版”等尾巴，也不能只替换一两个字。
        2. 中文剧名严格为 6-15 个汉字，使用标准、自然、通顺的表达，不带标点、引号和书名号。
        3. 优先体现冲突、关系反转、身份转变或命运逆转，但不得包含违背伦理道德、炫富拜金、宣扬极端复仇、色情低俗、擦边暗示或过度口语化的表达。
        4. 不得与 forbidden_titles 中任何一项相同或近似，不得与 occupied_exact_titles 完全相同；rewrite_variant_key 是差异化种子，同一原剧名多次改写时要给出不同表达。
        5. 新剧名会用于 AI 海报改字，优先使用常见、字形清晰的简体汉字，避开生僻字、繁体字、异体字和近形字：冤/宛/苑、赢/嬴、赘/整、娇/骄、婿/胥、虐/虎、孽/蘖、阔/闯、曦/羲、璟/景、衍/街、羁/羂、魇/厌、霁/齐、漾/羡。

        description（简介）：
        1. 只有 rewrite_synopsis 为 true 时才改写；为 false 时返回空字符串。
        2. 保留原简介的核心人物关系和冲突，突出爽点、反转和情绪卖点，换一种叙述，不要照抄 forbidden_synopses。
        3. 中文简介写成 100-300 个字，禁止出现任何演员姓名。

        themes（题材）：
        1. 只能从 theme_options 中选择 theme_count 个最贴合剧情的题材，不能重复，禁止自定义题材。

        audience（目标观众）：
        1. ask_audience 为 true 时只能返回“男”或“女”；为 false 时返回空字符串。

        language（输出语言）：
        1. 按 output_language 的要求输出 title 和 description，并返回实际使用的语言代码。

        只返回 JSON，不要解释，不要 Markdown：
        {"title":"","description":"","themes":[""],"audience":"","language":""}
        """;

    public const string KuaishouPublishInfoPromptPrevious = """
        你是快手分账短剧发布信息助手。根据原始短剧信息，一次生成个人版和企业版都要使用的发布信息。不生成推荐语。

        title（新剧名）：2 到 title_max_length 个字，不得包含空格、标点和书名号，必须与原剧名明显不同。这是硬性限制，输出前逐字计数，超长就压缩，不得用空格或标点规避。
        short_title（短标题）：6-16 个字，不得包含空格，不能与新剧名或原剧名相同。
        description（简介）：必须根据原简介改写，不能照抄。100-400 个字，以句号、问号或感叹号结束。写清人物关系、核心冲突和剧情转折，不得虚构原简介没有的人名、身份和剧情，不得重复句子。
        tags：3-6 个短标签，突出题材、关系或情绪，不带 #。
        audience：只能从 audience_options 中选一个。
        categories：从 category_options 中选 1-3 个，不能自造。
        plots：从 plot_options 中选 1-3 个，不能自造。
        新剧名、短标题、简介和标签不得包含炫富拜金、极端复仇、低俗擦边、侮辱挑衅等不良导向。
        必须遵守 compliance 中的快手内容合规要求：按 compliance.rule 避开 title_forbidden、synopsis_forbidden 中的词和 synopsis_forbidden_combinations 中的组合，输出前逐条自查。

        只返回 JSON，不要解释，不要 Markdown：
        {"title":"","short_title":"","description":"","tags":[""],"audience":"","categories":[""],"plots":[""]}
        """;

    public const string KuaishouPublishInfoPromptBeforeStrictValidation = """
        你是快手分账短剧发布信息助手。根据原始短剧信息，一次生成个人版和企业版都要使用的发布信息。不生成推荐语。

        title（新剧名）：2 到 title_max_length 个字，不得包含空格、标点和书名号，必须与原剧名明显不同。这是硬性限制，输出前逐字计数，超长就压缩，不得用空格或标点规避。
        short_title（短标题）：6-16 个字，不得包含空格，不能与新剧名或原剧名相同。
        description（简介）：必须根据原简介改写，不能照抄。100-400 个字，以句号结束。保留原简介里的人名和主线，用平实、正向的句子重写：写成守责、安民、整肃、同心、把危局稳住。冲突写成需要面对的困难，收束写成把局面安顿好。不写穿越、穿成、重生、系统、金手指、废物、赘婿、打脸、搬空国库、三日灭国、现代知识碾压古代、成立科学院；原简介里有这些，改成临危受命、整顿政务、安定百姓、守住基业。不得虚构原简介没有的人名、身份和剧情，不得重复句子，不照抄原简介的夸张句。
        tags：3-6 个短标签，只写题材和关系，不带 #。不写穿越、穿成、系统、金手指、废物、打脸、灭国。这些短标签不填进分类。
        audience：只能从 audience_options 中选一个。
        categories：只能从 category_options 中选 1-3 个，不能自造，也不能把情节名写成分类。分类只有脑洞、甜宠、逆袭、热血、复仇、家庭、乡村、古风、年代、穿越、悬疑、武侠、校园、搞笑、都市。原剧是穿越碾压、系统流或重生打脸时，不选「穿越」和「脑洞」；古装守国选「古风」，需要体现护国、担当时再加「热血」。「家庭」只在剧情确是家庭关系时选。
        plots：只能从 plot_options 中选 1-3 个，不能自造。魔改、系统、重生打脸这类剧，不选「穿越」「系统流」「重生转世」，改选列表里已有的「王朝架空」「成长奋斗」「传奇演义」。
        新剧名、短标题、简介和标签不得包含炫富拜金、极端复仇、低俗擦边、侮辱挑衅等不良导向。
        必须遵守 compliance 中的快手内容合规要求：按 compliance.rule 避开 title_forbidden、synopsis_forbidden 中的词和 synopsis_forbidden_combinations 中的组合，输出前逐条自查。

        只返回 JSON，不要解释，不要 Markdown：
        {"title":"","short_title":"","description":"","tags":[""],"audience":"","categories":[""],"plots":[""]}
        """;

    public const string KuaishouPublishInfoPrompt = """
        你负责为快手分账短剧整理发布资料。根据 original_title 和 synopsis 生成适合发布表单的信息。以原简介为事实来源，写清主要人物、他们面临的难题和已有的情节转折。

        title：用 2 至 title_max_length 个汉字拟定新剧名。不得与原剧名相同，不使用标点、空格、字母或数字。如果 keep_existing_title 非空，原样保留该值。
        short_title：写 6 至 16 个汉字，概括剧情看点。不得含空格，不得与原剧名或新剧名相同。
        description：改写为 100 至 400 字，以句号结束。保留原有的人物关系、关键事件、因果和结局。可以突出人物如何应对困难、做出选择，以及原作已有的转折。不得照抄、重复句子，或为凑字数补造人物、身份及剧情。
        tags：填写 3 个互不重复的短标签，从作品题材、人物关系和主要矛盾中提取。每项只填一个标签，不带 #、空格或分隔符。
        audience：从 audience_options 中原样选择 1 个最符合剧情的受众选项。
        categories：从 category_options 中原样选择 1 至 3 个分类，不得重复。优先选覆盖主线的分类。
        plots：从 plot_options 中原样选择 1 至 3 个情节，不得重复。每一项都要有原简介中的情节依据。

        用自然、克制的书面语表达。遵守输入 compliance 中的禁用词和组合规则。涉及敏感情节时，用合规表述保留剧情因果，不用近义词规避词库，不把炫富、羞辱、极端报复或低俗内容写成看点。若输入包含 retry.reason，先修正指出的问题，再检查全部字段。

        只返回一个合法 JSON 对象，不添加解释或 Markdown：
        {"title":"","short_title":"","description":"","tags":["","",""],"audience":"","categories":[""],"plots":[""]}
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

    public const string PosterModeDirect = "original";
    public const string PosterModeEraseFixedTitle = "erase_fixed_lower_title";
    public const string PosterModeReferenceRegenerate = ShortDrama.Core.Posters.PosterReferenceCover.Mode;

    public const string PosterAgentDirectPrompt = """
        基于输入短剧海报重新设计一张可直接发布的成品海报。
        必须彻底清除原图中的全部标题、字幕、人物名、演员名、作者及改编来源、宣传语、季数、水印、Logo文字和角标，只写一次新的海报标题“{title}”，成品中不得出现其他任何可读文字。
        必须同时更换背景并重新设计构图，不能只对原文字区域做局部修补。
        必须改变人物的肢体动作、服饰和脸部表情，但保持人物身份、主要面部特征、人物关系和短剧题材可识别。
        新标题使用标准、清晰、易识别的简体中文海报粗标题，逐字准确，醒目且占据主标题区域；禁止繁体字、异体字、错别字、手写体、花体和变形字。
        保持专业短剧海报质感，避免重复人物、畸形肢体、异常五官、额外文字、乱码和水印。
        """;

    public const string PosterAgentErasePrompt = """
        这是海报全图文字清理任务，不是重新设计或重绘海报。
        删除图片中所有可读文字和文字残影，包括主标题、副标题、人物或角色姓名、演员名、作者、改编或来源说明、版权或出品信息、宣传语、季数、字幕、水印、Logo文字、角标，以及任何中文、英文、拼音、字母和数字文字。
        用每处文字周围的背景、纹理和光影自然补全被删除区域，不能留下描边、底纹、模糊字或残影。
        不得生成任何新文字、符号、Logo、水印、印章或标记。
        人物、脸部、身体、服装、发型、动作、道具、背景、构图、尺寸、比例、颜色、光影和清晰度必须保持不变。
        最终输出必须是一张完全无文字的干净海报底图，新剧名由程序用固定字体写入。
        """;

    public const string PosterAgentSafeRetryPrefix = """
        这是安全合规的短剧海报设计任务：人物着装和动作保持得体，不包含暴力、血腥、裸露或低俗暗示；标题使用最普通、标准、清晰的简体中文印刷粗体，逐字准确。

        """;

    internal const string PreviousKuaishouPosterVerticalDirectPrompt = """
        基于输入短剧海报重新设计一张可直接发布的竖屏成品海报。
        必须彻底清除原图中的全部标题、字幕、人物名、演员名、作者及改编来源、宣传语、季数、水印、Logo文字和角标，只写一次新的海报标题“{title}”，成品中不得出现其他任何可读文字。
        必须同时更换背景并重新设计构图，不能只对原文字区域做局部修补。
        必须改变人物的肢体动作、服饰和脸部表情，但保持人物身份、主要面部特征、人物关系和短剧题材可识别。
        保持竖屏海报比例，不要改成横屏，不要加黑边、白边或画中画。
        新标题使用标准、清晰、易识别的简体中文海报粗标题，逐字准确，醒目且占据主标题区域；禁止繁体字、异体字、错别字、手写体、花体和变形字。
        保持专业短剧海报质感，避免重复人物、畸形肢体、异常五官、额外文字、乱码和水印。
        """;

    internal const string PreviousKuaishouPosterVerticalErasePrompt = """
        这是竖屏海报全图文字清理任务，不是重新设计或重绘海报。
        删除图片中所有可读文字和文字残影，包括主标题、副标题、人物或角色姓名、演员名、作者、改编或来源说明、版权或出品信息、宣传语、季数、字幕、水印、Logo文字、角标，以及任何中文、英文、拼音、字母和数字文字。
        用每处文字周围的背景、纹理和光影自然补全被删除区域，不能留下描边、底纹、模糊字或残影。
        不得生成任何新文字、符号、Logo、水印、印章或标记。
        人物、脸部、身体、服装、发型、动作、道具、背景、构图、尺寸、比例、颜色、光影和清晰度必须保持不变，并保持竖屏比例。
        最终输出必须是一张完全无文字的干净竖屏海报底图，新剧名由程序用固定字体写入。
        """;

    internal const string PreviousKuaishouPosterHorizontalDirectPrompt = """
        参考已完成的竖屏海报，生成一张 16:10 横屏短剧封面。
        最终画面必须是自然横屏构图，不能把竖屏海报压扁、拉宽或简单缩放到横屏，也不能做成竖屏画中画。
        保持人物、脸部、身体、道具和文字的真实比例，严禁横向拉伸、纵向压缩、头身比例变形、脸部变宽或文字被挤扁。
        以竖屏海报为视觉参考重新构图：保留主要人物、题材氛围、服装和关键场景，允许扩展左右两侧背景、环境和光影。
        主体人物位于画面中部偏上，脸部清晰，不要被裁掉，不要贴边；画面底部预留标题安全区。
        必须彻底清除原图中的全部旧标题和其他可读文字，只写一次完整剧名“{title}”，逐字准确，水平居中，字号足够大，不能遮挡脸部，不能贴边或被裁切。
        标题使用标准、清晰、易识别的简体中文印刷粗体，禁止繁体字、异体字、错别字、手写体、花体和变形字。
        不要添加黑边、白边、相框或截图边框。
        """;

    internal const string PreviousKuaishouPosterHorizontalErasePrompt = """
        参考这张没有文字的竖屏海报底图，生成一张 16:10 横屏短剧封面底图，供程序随后写入标题。
        最终画面必须是自然横屏构图，不能把竖屏海报压扁、拉宽、简单缩放，也不能做成竖屏画中画。
        保持人物、脸部、身体和道具的真实比例，严禁横向拉伸或头身比例变形。
        保留主要人物、题材氛围、服装和关键场景，允许扩展左右两侧背景、环境和光影。
        主体人物位于画面中部偏上，脸部清晰；画面底部保留干净、连续、适合添加标题的安全区。
        不得生成任何标题、汉字、拼音、英文、数字、Logo、水印、字幕或文字残影。
        人物身份、主要面部特征、服装、动作和清晰度保持不变。
        不要添加黑边、白边、相框或截图边框。
        """;

    public const string KuaishouPosterVerticalDirectPrompt = """
        以输入海报为人物与剧情参考，重新设计一张 3:4 竖屏短剧宣传海报。保留主要人物的身份、可识别的面部特征、人物关系和剧情主题。重新设计背景、构图和镜头，调整服饰、动作与表情，同时符合原剧情设定。使用电影感光影、清晰细节和有戏剧张力的场景。
        清理原图的旧标题、字幕、宣传文案、标识和其他干扰文字。画面只写一次新剧名「{title}」，逐字准确，采用清晰的宋体、黑体或楷体风格，避免艺术字、变形字和错别字。标题与人物脸部都要留出安全边距。避免文字残影、乱码、重复人物、多余角色、异常五官和畸形肢体。
        """;

    public const string KuaishouPosterVerticalErasePrompt = """
        以输入海报为人物与剧情参考，重新设计一张 3:4 竖屏无字底图。保留主要人物的身份、可识别的面部特征、人物关系和剧情主题。重新设计背景和构图，调整服饰、动作与表情。保持清晰细节、电影感光影和戏剧氛围。
        清理原画面的标题、字幕、宣传文案、标识和其他文字。最终不得出现文字、字母、数字、乱码或仿文字图形。底部留出与场景自然衔接的标题安全区，避免人物脸部和关键道具落入该区域。新剧名由程序在底图生成后写入。
        """;

    private const string KuaishouPosterHorizontalExpansionPrompt = """
        把输入画布扩展为完整的 16:10 横屏海报。保留中央的完整竖版画面，包括人物、底部与边缘的所有内容。原图只能整体等比放置，禁止裁剪、拉伸、遮挡、移动或修改任何内部元素。
        只补全原图以外的背景，延续已有场景、建筑、环境、光影、色彩与景深。让边界自然过渡，不要出现黑边、白边、模糊填充或拼接痕迹。
        """;

    public const string KuaishouPosterHorizontalDirectPrompt =
        KuaishouPosterHorizontalExpansionPrompt + "\n完整保留竖版画面中的新剧名「{title}」，扩展区域不得新增文字。";

    public const string KuaishouPosterHorizontalErasePrompt =
        KuaishouPosterHorizontalExpansionPrompt + "\n输入是竖屏无字底图。横屏输出也必须无字，保留底部标题区，新剧名由程序写入。";

    public static bool IsEraseFixedTitlePosterMode(string? mode) =>
        string.Equals(mode?.Trim(), PosterModeEraseFixedTitle, StringComparison.OrdinalIgnoreCase);

    public static string DefaultPosterAgentPrompt(string? mode) =>
        IsEraseFixedTitlePosterMode(mode) ? PosterAgentErasePrompt : PosterAgentDirectPrompt;

    public static string DefaultKuaishouPosterPrompt(bool horizontal, string? mode) =>
        (horizontal, IsEraseFixedTitlePosterMode(mode)) switch
        {
            (false, false) => KuaishouPosterVerticalDirectPrompt,
            (false, true) => KuaishouPosterVerticalErasePrompt,
            (true, false) => KuaishouPosterHorizontalDirectPrompt,
            _ => KuaishouPosterHorizontalErasePrompt,
        };

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
