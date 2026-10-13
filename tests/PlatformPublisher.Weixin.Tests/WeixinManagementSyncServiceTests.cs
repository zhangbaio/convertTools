using PlatformPublisher.Weixin.Publishing;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinManagementSyncServiceTests
{
    [Fact]
    public void BuildPayloadReadsProjectIdentityAndMaterialSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "platform-weixin-management-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "videos"));
            File.WriteAllText(Path.Combine(root, "短剧信息.txt"), "原剧名：原剧\n新剧名：新剧\n简介：剧情简介\n制作公司：测试公司\n时间（分钟）：120\n");
            File.WriteAllText(Path.Combine(root, "videos", "第1集.mp4"), "test");
            File.WriteAllText(Path.Combine(root, "海报图片.jpg"), "test");
            File.WriteAllText(Path.Combine(root, "成本报表.png"), "test");
            File.WriteAllText(Path.Combine(root, "工程图_1.png"), "test");

            var payload = WeixinManagementSyncService.BuildPayload(root, "是", "视频号A");

            Assert.Equal("原剧", payload["original_name"]!.GetValue<string>());
            Assert.Equal("新剧", payload["new_name"]!.GetValue<string>());
            Assert.Equal(1, payload["episodes"]!.GetValue<int>());
            Assert.Equal("是", payload["uploaded"]!.GetValue<string>());
            Assert.Equal("视频号A", payload["uploader"]!.GetValue<string>());
            Assert.Contains("工程图:1张", payload["materials"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
