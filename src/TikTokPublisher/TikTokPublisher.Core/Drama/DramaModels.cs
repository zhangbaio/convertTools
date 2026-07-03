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
}

public sealed class DramaDownloadQueueState
{
    public const string SettingKey = "drama_download_queue_state";

    public int Version { get; set; } = 3;
    public string WorkspacePath { get; set; } = "";
    public List<DramaDownloadQueueItem> QueueItems { get; set; } = new();
    public bool AutoGenerateMaterials { get; set; } = true;
    public int DownloadConcurrent { get; set; } = 3;
    public string DownloadEpisodeNumberMode { get; set; } = "source";
    public string DefaultQuality { get; set; } = "1080P";
}
