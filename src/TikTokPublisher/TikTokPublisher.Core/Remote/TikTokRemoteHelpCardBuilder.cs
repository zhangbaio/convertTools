using System.Text.Encodings.Web;
using System.Text.Json;

namespace TikTokPublisher.Core.Remote;

public static class TikTokRemoteHelpCardBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string BuildCommandTutorialCardJson()
    {
        var card = new
        {
            config = new
            {
                wide_screen_mode = true,
            },
            header = new
            {
                template = "blue",
                title = new
                {
                    tag = "plain_text",
                    content = "TikTok 上传命令教程",
                },
            },
            elements = new object[]
            {
                Markdown("""
                    **上传 TikTok 剧集**
                    @机器人 上传剧集 剧名A

                    或：
                    @机器人 上传TikTok
                    剧名A
                    剧名B
                    """),
                Markdown("""
                    **常用参数**
                    工作目录: E:\tiktok
                    账号: 默认
                    步骤: download,rewrite_info,generate_poster,upload_series
                    自动执行: 是
                    """),
                Markdown("""
                    **多账号**
                    账号: 账号A,账号B
                    账号: 全部

                    多账号执行会使用每个账号「基础设置」里保存的工作目录。
                    """),
                new
                {
                    tag = "action",
                    actions = new object[]
                    {
                        Button("查看状态", "状态"),
                        Button("执行队列", "执行队列"),
                        Button("文本教程", "教程"),
                    },
                },
            },
        };

        return JsonSerializer.Serialize(card, JsonOptions);
    }

    private static object Markdown(string content) => new
    {
        tag = "markdown",
        content = content.Trim(),
    };

    private static object Button(string label, string command) => new
    {
        tag = "button",
        text = new
        {
            tag = "plain_text",
            content = label,
        },
        type = "default",
        value = new
        {
            command,
        },
    };
}
