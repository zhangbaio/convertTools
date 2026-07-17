namespace TikTokPublisher.Core.Drama;

public sealed class DramaSearchItem
{
    public string BookId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public int EpisodeTotal { get; set; }
    public string Intro { get; set; } = "";
    public string PosterUrl { get; set; } = "";
    public string PublishTime { get; set; } = "";
    public string Author { get; set; } = "";
    public int FavoriteCount { get; set; }
    public bool Selected { get; set; }
    public string SourceMode { get; set; } = "";
}

public sealed class DramaDownloadQueueItem
{
    public string Title { get; set; } = "";
    public string BookId { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public string Episodes { get; set; } = "all";
    public string Quality { get; set; } = "1080P";
    public string EpisodeNumberMode { get; set; } = "source";
    public string Status { get; set; } = "待下载";
    public string Progress { get; set; } = "0%";
    public string Speed { get; set; } = "0 KB/s";
    public string StatusDetail { get; set; } = "";
    public bool GenerateMaterials { get; set; } = true;
    public string LastError { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string QueueEntrySource { get; set; } = "download_queue";
    public string QueueEntryDramaType { get; set; } = "";
    public string SourceMode { get; set; } = "";
    public string Author { get; set; } = "";
    public string Category { get; set; } = "";
    public int EpisodeTotal { get; set; }
    public int FavoriteCount { get; set; }
    public string PublishTime { get; set; } = "";
    public string PosterUrl { get; set; } = "";
}

public sealed class DramaDownloadQueueState
{
    public const string SettingKey = "drama_download_queue_state";
    public const int CurrentVersion = 6;
    public const string DefaultAuthorExclude =
        "掌玩,九州,河马,红果,快创,麦芽,花生,点众,天桥,中文在线,阅文,起点,红袖,17K,七猫,橙光," +
        "FlickReels,ShortTV,云起剧场,甜柚剧场,听花岛,星辰短剧推荐,星芒剧场,晚风微剧,青禾短剧,桃喜微剧,南栀剧场," +
        "云禾剧场,清欢微剧,遇见好剧,点点甜剧,泡面短剧,微剧吧官方,彩虹影院,月光短剧,青山剧院,双星剧场,倩儿剧场," +
        "荔香短剧,云樱小剧场,等闲剧场,玄境漫剧场,星途微剧,墨染古风剧场,漫云剧场,黑糖短剧,摩卡微剧,燕麦微剧," +
        "芝士短剧,生椰短剧,白桃短剧,锡兰剧场,山楂小剧场,布丁微剧,冻柠微剧,拿铁短剧,红桃小剧场,漫故事";

    public int Version { get; set; } = CurrentVersion;
    public string WorkspacePath { get; set; } = "";
    public List<DramaDownloadQueueItem> QueueItems { get; set; } = new();
    public bool AutoGenerateMaterials { get; set; } = true;
    public int DownloadConcurrent { get; set; } = 5;
    public string DownloadEpisodeNumberMode { get; set; } = "source";
    public string DefaultQuality { get; set; } = "1080P";
    public string CategoryInclude { get; set; } = "";
    public string CategoryExclude { get; set; } = "";
    public string AuthorExclude { get; set; } = DefaultAuthorExclude;
}
