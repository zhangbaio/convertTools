using System.Text;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoPureSignTests
{
    private const string Url =
        "https://api5-normal-sinfonlinea.fqnovel.com/reading/bookapi/search/tab/v?aid=8662&app_name=novelread&version_code=72232&version_name=7.2.2.32&channel=audit&device_platform=android&device_type=audit-windows&os_version=12&update_version_code=72232&device_id=1234567890123456&query=%E9%95%BF%E5%85%AC%E4%B8%BB&tab_name=feed&search_source=1&offset=0&count=10&use_correct=true&_rticket=1700000000000";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        "{\"biz_param\":{\"detail_page_version\":0,\"disable_digg_stat\":false},\"dr_scene\":\"preload\",\"series_id\":\"1\"}");

    private static readonly HongguoPureSign.MedusaEntropy Entropy = new(
        [7, 7, 7, 7], 103, 10004, 1700000000, 0x01020304, 0x01020304, "testnonce", 1700000000000);

    [Fact]
    public void Sm3MatchesKnownDigest()
    {
        var digest = Convert.ToHexString(HongguoSm3.Hash("abc"u8.ToArray())).ToLowerInvariant();
        Assert.Equal("66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0", digest);
    }

    [Fact]
    public void SignMatchesPythonGolden()
    {
        Assert.Equal(1, HongguoPureSign.BranchOf(Url, Body, 1700000000));
        Assert.Equal("8404030040012e92d571716f0b1fc20bbee62310b829b712be8b", HongguoPureSign.XGorgon(Url[(Url.IndexOf('?') + 1)..], Body, 1700000000, 3));
        Assert.Equal("BAMCAaBPXV/+KNBRDXGiYSJtXJS2Wy6kFGNFrIJDsc+FCdVp", HongguoPureSign.HeliosWith(1700000000, 0x01020304));
        var post = HongguoPureSign.Sign(Url, Body, 1700000000, 3, 0x01020304, Entropy);
        Assert.Equal("ZVPxAA==", post["x-ladon"]);
        Assert.Equal("APFTZQ==", post["x-argus"]);
        Assert.Equal("A/FTZfcZDJ/XJo9e1tubFVc+Mn0EAwAByBUEAwIBA/OMGCq0xjqLP6DAMI14blBby7F3ZjPTX5V99nHVo9UXz2KxHLyLmcj4BVj+2HJmeUHggc3poxlSrDQkGx5QsoKt/4W7SUQ2+OKSKYAsSRIzxUgD0fCS8v8XCjnADABu9CCOpUn54psB3EmDtXqajl7Ir/9GuFLgbJHUiyVSCHgnfiEC5YJ+99dDtyaMip+kdUj7jVcWsum8iZZhFuSWTxogdxHY01mLoyYVHwt9tRU7HpekKB4Q1IoYsxB83zd0b4AoMzpaYF0bZBBR2MPq1IToQOu3nOTUhAxMXtUFs635f1HJDhPtWtnHFDzbcZcdRkd40IjarX0NQ363lXqV6peM9QNWHfTda+V7DioQEiSrs7Mux6m23OFfL4go8SkKZ2HiFvKb0UhUCo9o4vBlv9mU7vzbzKq7qZrkJMtQVcMMSCRtng6lqDQ2xB1uT2Tn7N7p1MdEjDL/KcAb2v+zzMeMhb2IsW+9yak3UYEHIyU6Op2jO7XdghQBg8ok5vW03nwVD1TeHJJ/UPp99GoFdjyr1OVRR9ffVM8ex5Jz/DEBN7bSteUeA6bMrffycPffvrBcdB5mChTil26wGDYr2bzXwElx+tpqmGnB14yDAjMC3MJUiy52IybFJnRmKcd2Dy0vMGNYxtVmi1TK0QTaaS+wIXbZcxFA4RUdAmtxONAtFnoSf2uFk0b9DyjgNGIMtmn6gqepGcibywRZW28H6qBU2yZT4hD/StIWOIUUcSzw/xVX35YUPqza6ZgGGaaFZaDdvhOZWjvkG98Mxz8mG16sUfngwEWb/jvQROTu3YXj3337M+sIwnlOwPIRjC4AOJXGh+L/nFaTiZxRkqYbFZzyLy+tP0pOP5AfNY5adWzwo01OVevWToNl9cZvcFv5wYC2QaoahTWS0Atrg4hVIyCzlrwRUFiJ7/F7IDFO7/z//+/8/r8CAQ==", post["x-medusa"]);
        var get = HongguoPureSign.Sign(Url, null, 1700000000, 3, 0x01020304, Entropy);
        Assert.Equal("8404030040012e92d504d9180ec7c20bbee62310b829b712be8b", get["x-gorgon"]);
        Assert.Equal("A/FTZfcZDJ/XJo9e1tubFVc+Mn0EAwAB/TVEAxIBAfcMGPctRb0kzkzHDH9babdWOBqHvqV71FHm9vlD/7XgSIE1C5yb5Xsg5rwmkJjhudsCjTu0L0zcZoyMlwbcAtuvWKprOG9G3tvv3xq+R+iQzsFiozSmq2khdctpBaM84cs08ugRAiTeg3zjcS60wzx5erE6tVYnXDi0/yoLF0ACWcD8i4tGHC1PVkizErzM7AqQIO+Z4DR7PwvYAhanat7FJuMCrt8e7/iTFL3351Lc7yIWqeP8OubOvIY6CD01MuxSmTNu1IyY+awupSIxKVnFkse2rH44Yu0IPNt1hx1ERnjQiN6tfQ9CfpfVepXql411A1Yd9N1r5XsOKhASJKuzsy7Hqbbc4V8viCjxKQpnYeIW8pvRSFQKj2ji8GW/2ZTu/NvMqrupmuQky1BVwwxIJG2eDqWoNDbEHW5PZOfs3unUx0SMMv8pwBva/7PMx4yFvYixb73JqTdRgQcjJTo6naM7td2CFAGDyiTm9bTefBUPVN4ckn9Q+n30agV2PKvU5VFH199Uzx7HknP8MQE3ttK15R4Dpsyt9/Jw99++sFx0HmYKFOKXbrAYNivZvNfASXH62mqYacHXjIMCMwLcwlSLLnYjJsUmdGYpx3YPLS8wY1jG1WaLVMrRBNppL7AhdtlzEUDhFR0Ca3E40C0WehJ/a4WTRv0PKOA0Ygy2afqCp6kZyJvLBFlbbwfqoFTbJlPiEP9K0hY4hRRxLPD/FVfflhQ+rNrpmAYZpoVloN2+E5laO+Qb3wzHPyYbXqxR+eDARZv+O9BE5O7dhePfffsz6wjCeU7A8hGMLgA4lcaH4v+cVpOJnFGSphsVnPIvL60/Sk4/kB81jlp1bPCjTU5V69ZOg2X1xm9wW/nBgLZBqhqFNZLQC2uDiFUjILOWvBFQWInv8XsgMcXv/P//7/z+vwIB", get["x-medusa"]);
    }
}
