using FluentAssertions;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public class DramaBoxPrivateMp4DecryptorTests
{
    [Fact]
    public void Wrapping_material_uses_expires_and_blank_video_id()
    {
        var package = new DramaBoxPrivateMp4Decryptor.KeyPackage(
            [],
            [],
            [],
            "bm9uY2U",
            "uri",
            "abc",
            "trial",
            2,
            0,
            1792488883,
            "");

        DramaBoxPrivateMp4Decryptor.BuildWrappingMaterial(package, "dr_downloader_cpp", "device")
            .Should().Be("dr-private-mp4-key-v2\ndr_downloader_cpp\ndevice\n1\n\nabc\nbm9uY2U\n1792488883\ntrial");
    }
}
