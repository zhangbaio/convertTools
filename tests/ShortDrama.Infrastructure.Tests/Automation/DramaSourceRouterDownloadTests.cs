using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class DramaSourceRouterDownloadTests
{
    [Fact]
    public void Mp4_structure_validation_rejects_truncated_mdat()
    {
        var path = Path.Combine(Path.GetTempPath(), $"truncated-mp4-{Guid.NewGuid():N}.mp4");
        try
        {
            File.WriteAllBytes(path, BuildMp4WithDeclaredMdatSize(1024, actualPayloadSize: 16));

            DramaSourceRouter.HasCompleteMp4Structure(path).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Mp4_structure_validation_accepts_complete_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"complete-mp4-{Guid.NewGuid():N}.mp4");
        try
        {
            File.WriteAllBytes(path, BuildMp4WithDeclaredMdatSize(24, actualPayloadSize: 16));

            DramaSourceRouter.HasCompleteMp4Structure(path).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Encrypted_mp4_sample_entry_is_detected_for_forced_replacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"encrypted-mp4-{Guid.NewGuid():N}.mp4");
        try
        {
            var bytes = BuildMp4WithDeclaredMdatSize(28, actualPayloadSize: 20);
            "encv"u8.CopyTo(bytes.AsSpan(24, 4));
            File.WriteAllBytes(path, bytes);

            DramaSourceRouter.ContainsEncryptedMp4SampleEntry(path).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("mapleleaf", 60, 900)]
    [InlineData("MAPLELEAF", 600, 900)]
    [InlineData("hghigh", 60, 60)]
    [InlineData("hgnew", 5, 10)]
    public void Provider_download_timeout_prevents_mapleleaf_slow_cdn_restart_loops(
        string source,
        int configuredSeconds,
        int expectedSeconds)
    {
        DramaSourceRouter.ResolveProviderDownloadTimeoutSeconds(source, configuredSeconds)
            .Should().Be(expectedSeconds);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(60, 15)]
    [InlineData(10, 10)]
    [InlineData(2, 5)]
    public void Play_url_resolution_uses_reference_client_short_timeout(
        int configuredSeconds,
        int expectedSeconds)
    {
        DramaSourceRouter.ResolvePlayUrlTimeoutSeconds(configuredSeconds)
            .Should().Be(expectedSeconds);
    }

    [Fact]
    public void High_Playback_Timeout_Should_Be_Retryable_But_Authentication_Should_Not()
    {
        DramaSourceRouter.ShouldRetryDownload(
                new HongguoHighException("高码率播放地址解析超过 15 秒，已停止等待", 408))
            .Should().BeTrue();
        DramaSourceRouter.ShouldRetryDownload(
                new HongguoHighException("token 已失效", 401))
            .Should().BeFalse();
    }

    [Fact]
    public void Retry_Delay_Should_Match_Reference_Client_Backoff()
    {
        DramaSourceRouter.ResolveDownloadRetryDelay(
                new HongguoHighException("高码率解析服务繁忙，请稍后重试"), 1)
            .Should().Be(TimeSpan.FromSeconds(5));
        DramaSourceRouter.ResolveDownloadRetryDelay(
                new HongguoHighException("高码率解析服务繁忙，请稍后重试"), 3)
            .Should().Be(TimeSpan.FromSeconds(20));
        DramaSourceRouter.ResolveDownloadRetryDelay(new IOException("timeout"), 2)
            .Should().Be(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("第10集.mp4", 10, true)]
    [InlineData("第010集.mp4", 10, true)]
    [InlineData("第9集.mp4", 10, false)]
    [InlineData("片段_第10集.mp4", 10, false)]
    public void Existing_episode_match_only_accepts_the_current_output_number(
        string fileName,
        int outputEpisodeNumber,
        bool expected)
    {
        DramaSourceRouter.IsEpisodeFileForOutput(fileName, outputEpisodeNumber).Should().Be(expected);
    }

    [Fact]
    public async Task TryBuildSuccessfulResultWhenVideosExist_Should_Return_Null_When_OutputDir_Has_No_Videos()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "测试剧",
                BookId: "book-1",
                Episodes: "all",
                Quality: "1080P",
                Concurrent: 3,
                EpisodeNumberMode: "source");

            (await DramaSourceRouter.TryBuildSuccessfulResultWhenVideosExistAsync(
                request,
                progress: null,
                CancellationToken.None)).Should().BeNull();
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task TryBuildSuccessfulResultWhenVideosExist_Should_Return_Success_When_Videos_Already_Present()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-videos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(outputDir, "第01集.mp4"), [1, 2, 3]);

        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) =>
                throw new InvalidOperationException("红果新接口已有文件检查不应启动编码校验。");
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "测试剧",
                BookId: "book-1",
                Episodes: "all",
                Quality: "1080P",
                Concurrent: 3,
                EpisodeNumberMode: "source");

            var result = await DramaSourceRouter.TryBuildSuccessfulResultWhenVideosExistAsync(
                request,
                progress: null,
                CancellationToken.None);

            result.Should().NotBeNull();
            result!.Ok.Should().BeTrue();
            result.VideoCount.Should().Be(1);
            result.Message.Should().Contain("跳过 legacy 红果下载重试");
        }
        finally
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Not_Probe_Codec_For_Hgnew()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hgnew-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new HgnewDownloadRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var progress = new RecordingProgress();

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) =>
                throw new InvalidOperationException("红果新接口下载不应启动 ffprobe 或 ffmpeg。");

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hgnew",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HgnewAccount = "test@example.com",
                HgnewPassword = "secret",
                HgnewUdid = "42ce0f9242ea893b241749e35cf894be",
                HgnewClientVersion = "1.5.0"
            };
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "hgnew-drama",
                BookId: "book-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await CreateRouter(httpClient, settings).DownloadAsync(
                request,
                progress,
                CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllBytes(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject)
                .Should().Equal(HgnewDownloadRecordingHandler.VideoBytes);
            progress.Messages.Should().NotContain(message =>
                message.Contains("视频校验", StringComparison.Ordinal) ||
                message.Contains("视频编码", StringComparison.Ordinal));
            progress.Messages.Should().Contain(message => message.Contains("保留源文件", StringComparison.Ordinal));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("<html>upstream error</html>")]
    [InlineData("{\"error\":\"unauthorized\"}")]
    [InlineData("#EXTM3U\nhttps://example.com/segment.ts")]
    public async Task DownloadAsync_Should_Reject_NonMedia_Hgnew_Body_Without_Probing_Codec(string responseBody)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hgnew-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new HgnewDownloadRecordingHandler(Encoding.UTF8.GetBytes(responseBody), mediaType: null);
        using var httpClient = new HttpClient(handler);
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) =>
                throw new InvalidOperationException("红果新接口基础文件体检不应启动编码校验。");
            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hgnew",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HgnewAccount = "test@example.com",
                HgnewPassword = "secret",
                HgnewUdid = "42ce0f9242ea893b241749e35cf894be",
                HgnewClientVersion = "1.5.0"
            };
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "hgnew-invalid-drama",
                BookId: "book-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await CreateRouter(httpClient, settings).DownloadAsync(
                request,
                progress: null,
                CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.Message.Should().Contain("下载内容不是有效的视频文件");
            Directory.GetFiles(outputDir, "*.mp4").Should().BeEmpty();
            Directory.GetFiles(outputDir, "*.part").Should().BeEmpty();
        }
        finally
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Not_Probe_Codec_For_Downloader()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-downloader-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        using var httpClient = new HttpClient(new DownloaderInvalidVideoHandler());
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) =>
                throw new InvalidOperationException("Downloader 链路不应启动编码校验或转码。");
            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "downloader",
                DownloaderApiBaseUrl = "http://127.0.0.1:17891",
                DownloaderApiKey = "gateway-key",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                DownloadFileSegments = "1"
            };
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "downloader-invalid-drama",
                BookId: "downloader:series-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source",
                ExistingVideoPolicy: ExistingVideoPolicy.ReplaceAll);

            var result = await CreateRouter(httpClient, settings).DownloadAsync(
                request,
                progress: null,
                CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle();
            Directory.GetFiles(outputDir, "*.part").Should().BeEmpty();
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("none", false)]
    [InlineData("UNKNOWN", false)]
    [InlineData("h264", true)]
    [InlineData("hevc", true)]
    public void IsRecognizedVideoCodec_rejects_missing_or_unknown_codecs(string? codec, bool expected)
    {
        DramaSourceRouter.IsRecognizedVideoCodec(codec).Should().Be(expected);
    }

    [Fact]
    public async Task DownloadAsync_Should_Use_Local_Stream_And_Transcode_Hevc_To_H264()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hglocal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfmpegResolver = DramaSourceRouter.ResolveFfmpegBinaryForTests.Value;
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = () => "fake-ffmpeg";
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = async (startInfo, cancellationToken) =>
            {
                if (startInfo.FileName == "fake-ffprobe")
                {
                    return startInfo.ArgumentList[^1].EndsWith(".h264.mp4", StringComparison.OrdinalIgnoreCase)
                        ? ProbeResult("h264")
                        : ProbeResult("hevc");
                }

                startInfo.FileName.Should().Be("fake-ffmpeg");
                startInfo.ArgumentList.Should().Contain("libx264");
                startInfo.ArgumentList.Should().Contain("aac");
                var inputPath = startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("-i") + 1];
                File.ReadAllBytes(inputPath).Should().Equal(LocalStreamRecordingHandler.StreamBytes);
                var outputPath = startInfo.ArgumentList[^1];
                outputPath.Should().EndWith(".h264.mp4");
                await File.WriteAllBytesAsync(outputPath, Encoding.UTF8.GetBytes("h264-video"), cancellationToken);
                return new DramaSourceRouter.ProcessRunResult(0, "", "");
            };

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hglocal",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HongguoLocalBaseUrl = "https://local.example.com",
                HongguoLocalApiKey = "local-key",
                HongguoLocalDownloadMode = "compatible",
                HongguoLocalTranscodeEngine = "cpu"
            };
            var router = new DramaSourceRouter(
                httpClient,
                new TestDramaSettingsProvider(settings),
                new HongguoLocalApiService(httpClient),
                new HongguoNewApiService(httpClient),
                new HongguoDramaSearchService(httpClient),
                new HongguoDramaDownloader(httpClient),
                new HongguoMemoryReaderService());

            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "test-drama",
                BookId: "hglocal:series-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await router.DownloadAsync(request, progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            result.VideoCount.Should().Be(1);
            var videoPath = Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject;
            File.ReadAllText(videoPath).Should().Be("h264-video");
            handler.Requests.Should().Contain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/video_url", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().Contain(request =>
                string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().NotContain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/stream", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = previousFfmpegResolver;
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Use_Configured_Range_Segments_And_Preserve_Content()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-range-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var videoBytes = BuildLargeVideoBytes();
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = videoBytes,
            SupportsRanges = true
        };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var progress = new RecordingProgress();

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (startInfo, _) =>
            {
                startInfo.FileName.Should().Be("fake-ffprobe");
                return Task.FromResult(ProbeResult("h264"));
            };

            var result = await CreateRouter(
                    httpClient,
                    CreateLocalSettings(downloadMode: "fast", fileSegments: "4"))
                .DownloadAsync(CreateDownloadRequest(outputDir), progress, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllBytes(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject)
                .Should().Equal(videoBytes);
            handler.GetCdnRanges().Should().HaveCount(5, "应包含 1 次 Range 探测和 4 个数据分块");
            handler.GetCdnRanges().Count(range => range.From == 0 && range.To == 0).Should().Be(1);
            handler.GetCdnRequests().Should().OnlyContain(request =>
                request.Headers.AcceptEncoding.Any(value =>
                    string.Equals(value.Value, "identity", StringComparison.OrdinalIgnoreCase)));
            handler.GetCdnRequests().Should().OnlyContain(request =>
                string.Equals(request.Headers.UserAgent.ToString(), "Mozilla/5.0", StringComparison.Ordinal));
            handler.GetCdnDataRangeRequests().Should().OnlyContain(request => request.Headers.IfRange == null);
            progress.Messages.Should().Contain(message => message.Contains("启用 4 路分块下载", StringComparison.Ordinal));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Retry_Range_Instead_Of_Falling_Back_To_Single_Stream()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-range-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var videoBytes = BuildLargeVideoBytes();
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = videoBytes,
            SupportsRanges = true,
            RejectOneDataRange = true
        };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var progress = new RecordingProgress();

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) => Task.FromResult(ProbeResult("h264"));

            var result = await CreateRouter(
                    httpClient,
                    CreateLocalSettings(downloadMode: "fast", fileSegments: "4"))
                .DownloadAsync(CreateDownloadRequest(outputDir), progress, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllBytes(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject)
                .Should().Equal(videoBytes);
            handler.GetCdnRequestsWithoutRange().Should().BeEmpty();
            handler.GetCdnDataRangeRequests().Should().HaveCount(5, "one of four ranges should be retried once");
            progress.Messages.Should().NotContain(message => message.Contains("切换单流下载", StringComparison.Ordinal));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Retry_A_Transient_Mismatched_ContentRange()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-range-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var videoBytes = BuildLargeVideoBytes();
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = videoBytes,
            SupportsRanges = true,
            ReturnMismatchedContentRangeOnce = true,
        };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) => Task.FromResult(ProbeResult("h264"));

            var result = await CreateRouter(
                    httpClient,
                    CreateLocalSettings(downloadMode: "fast", fileSegments: "4"))
                .DownloadAsync(CreateDownloadRequest(outputDir), progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllBytes(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject)
                .Should().Equal(videoBytes);
            handler.MismatchedContentRangeResponses.Should().Be(1);
            handler.GetCdnDataRangeRequests().Should().HaveCount(5, "one of four data ranges should be retried once");
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Reject_Unexpected_Partial_Response_In_Single_Stream_Mode()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = BuildLargeVideoBytes(),
            ReturnPartialForRequestsWithoutRange = true,
        };
        using var httpClient = new HttpClient(handler);

        try
        {
            var result = await CreateRouter(
                    httpClient,
                    CreateLocalSettings(downloadMode: "fast", fileSegments: "1"))
                .DownloadAsync(CreateDownloadRequest(outputDir), progress: null, CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.Message.Should().Contain("意外返回 HTTP 206");
            Directory.GetFiles(outputDir, "*.mp4").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Delete_Part_File_When_Segmented_Download_Is_Cancelled()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-range-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = BuildLargeVideoBytes(),
            SupportsRanges = true,
            BlockDataRangesUntilCancelled = true
        };
        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) =>
                throw new InvalidOperationException("取消发生在媒体校验之前。");
            var downloadTask = CreateRouter(
                    httpClient,
                    CreateLocalSettings(downloadMode: "fast", fileSegments: "4"))
                .DownloadAsync(CreateDownloadRequest(outputDir), progress: null, cancellation.Token);

            await handler.DataRangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            var action = async () => await downloadTask;
            await action.Should().ThrowAsync<OperationCanceledException>();
            Directory.GetFiles(outputDir, "*.part").Should().BeEmpty();
            Directory.GetFiles(outputDir, "*.encrypted.part").Should().BeEmpty();
        }
        finally
        {
            cancellation.Cancel();
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Use_Nvenc_When_Local_Compatible_Mode_Is_Auto_And_Nvenc_Available()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hglocal-nvenc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfmpegResolver = DramaSourceRouter.ResolveFfmpegBinaryForTests.Value;
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var sawEncoderProbe = false;
        var sawNvencTranscode = false;
        var sawCpuTranscode = false;

        try
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = () => "fake-ffmpeg";
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = async (startInfo, cancellationToken) =>
            {
                if (startInfo.FileName == "fake-ffprobe")
                {
                    return startInfo.ArgumentList[^1].EndsWith(".h264.mp4", StringComparison.OrdinalIgnoreCase)
                        ? ProbeResult("h264")
                        : ProbeResult("hevc");
                }

                startInfo.FileName.Should().Be("fake-ffmpeg");
                if (startInfo.ArgumentList.Contains("-encoders"))
                {
                    sawEncoderProbe = true;
                    return new DramaSourceRouter.ProcessRunResult(0, " V....D h264_nvenc NVIDIA NVENC H.264 encoder", "");
                }

                if (startInfo.ArgumentList.Contains("libx264"))
                {
                    sawCpuTranscode = true;
                }

                sawNvencTranscode = true;
                startInfo.ArgumentList.Should().Contain("hevc_cuvid");
                startInfo.ArgumentList.Should().Contain("h264_nvenc");
                startInfo.ArgumentList.Should().Contain("-cq");
                startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("-cq") + 1].Should().Be("26");
                startInfo.ArgumentList.Should().Contain("aac");
                var inputPath = startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("-i") + 1];
                File.ReadAllBytes(inputPath).Should().Equal(LocalStreamRecordingHandler.StreamBytes);
                await File.WriteAllBytesAsync(startInfo.ArgumentList[^1], Encoding.UTF8.GetBytes("nvenc-video"), cancellationToken);
                return new DramaSourceRouter.ProcessRunResult(0, "", "");
            };

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hglocal",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HongguoLocalBaseUrl = "https://local.example.com",
                HongguoLocalApiKey = "local-key",
                HongguoLocalDownloadMode = "compatible",
                HongguoLocalTranscodeEngine = "auto"
            };
            var router = new DramaSourceRouter(
                httpClient,
                new TestDramaSettingsProvider(settings),
                new HongguoLocalApiService(httpClient),
                new HongguoNewApiService(httpClient),
                new HongguoDramaSearchService(httpClient),
                new HongguoDramaDownloader(httpClient),
                new HongguoMemoryReaderService());

            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "test-drama",
                BookId: "hglocal:series-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await router.DownloadAsync(request, progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllText(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject).Should().Be("nvenc-video");
            sawEncoderProbe.Should().BeTrue();
            sawNvencTranscode.Should().BeTrue();
            sawCpuTranscode.Should().BeFalse();
        }
        finally
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = previousFfmpegResolver;
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Fall_Back_To_Cpu_When_Forced_Nvenc_Is_Unavailable()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hglocal-nvenc-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfmpegResolver = DramaSourceRouter.ResolveFfmpegBinaryForTests.Value;
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var sawCpuTranscode = false;

        try
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = () => "fake-ffmpeg";
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = async (startInfo, cancellationToken) =>
            {
                if (startInfo.FileName == "fake-ffprobe")
                {
                    return startInfo.ArgumentList[^1].EndsWith(".h264.mp4", StringComparison.OrdinalIgnoreCase)
                        ? ProbeResult("h264")
                        : ProbeResult("hevc");
                }

                if (startInfo.ArgumentList.Contains("-encoders"))
                {
                    return new DramaSourceRouter.ProcessRunResult(0, " V..... libx264", "");
                }

                startInfo.ArgumentList.Should().Contain("libx264");
                startInfo.ArgumentList.Should().NotContain("h264_nvenc");
                sawCpuTranscode = true;
                await File.WriteAllTextAsync(startInfo.ArgumentList[^1], "cpu-video", cancellationToken);
                return new DramaSourceRouter.ProcessRunResult(0, "", "");
            };

            var progress = new RecordingProgress();
            var result = await CreateRouter(
                    httpClient,
                    CreateLocalSettings(downloadMode: "compatible", transcodeEngine: "nvenc"))
                .DownloadAsync(CreateDownloadRequest(outputDir), progress, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            sawCpuTranscode.Should().BeTrue();
            File.ReadAllText(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject).Should().Be("cpu-video");
            progress.Messages.Should().Contain(message => message.Contains("CPU libx264", StringComparison.Ordinal));
        }
        finally
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = previousFfmpegResolver;
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Keep_Hglocal_Hevc_When_Mode_Is_Fast()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hglocal-fast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var sawFfprobe = false;

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (startInfo, _) =>
            {
                startInfo.FileName.Should().Be("fake-ffprobe", "fast mode should validate media but never run ffmpeg");
                sawFfprobe = true;
                return Task.FromResult(ProbeResult("hevc"));
            };

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hglocal",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HongguoLocalBaseUrl = "https://local.example.com",
                HongguoLocalApiKey = "local-key"
            };
            var router = new DramaSourceRouter(
                httpClient,
                new TestDramaSettingsProvider(settings),
                new HongguoLocalApiService(httpClient),
                new HongguoNewApiService(httpClient),
                new HongguoDramaSearchService(httpClient),
                new HongguoDramaDownloader(httpClient),
                new HongguoMemoryReaderService());

            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "test-drama",
                BookId: "hglocal:series-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await router.DownloadAsync(request, progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            sawFfprobe.Should().BeTrue();
            var videoPath = Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject;
            File.ReadAllBytes(videoPath).Should().Equal(LocalStreamRecordingHandler.StreamBytes);
            handler.Requests.Should().Contain(request =>
                string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().NotContain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/stream", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Keep_H264_Without_Starting_Ffmpeg()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-h264-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var h264Bytes = Encoding.UTF8.GetBytes("h264-source-video");
        var handler = new LocalStreamRecordingHandler { DownloadBytes = h264Bytes };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var progress = new RecordingProgress();

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (startInfo, _) =>
            {
                startInfo.FileName.Should().Be("fake-ffprobe", "H.264 media should not start ffmpeg");
                return Task.FromResult(ProbeResult("h264"));
            };

            var settings = CreateLocalSettings(downloadMode: "compatible", transcodeEngine: "cpu");
            var result = await CreateRouter(httpClient, settings).DownloadAsync(
                CreateDownloadRequest(outputDir),
                progress,
                CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllBytes(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject).Should().Equal(h264Bytes);
            progress.Messages.Should().Contain(message => message.Contains("编码 H264", StringComparison.Ordinal) &&
                                                          message.Contains("无需转码", StringComparison.Ordinal));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Reject_Html_Response_And_Not_Create_Mp4()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-html-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = Encoding.UTF8.GetBytes("<html>not a video</html>"),
            DownloadMediaType = "text/html"
        };
        using var httpClient = new HttpClient(handler);
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) =>
                throw new InvalidOperationException("HTML responses should be rejected before ffprobe");

            var result = await CreateRouter(httpClient, CreateLocalSettings()).DownloadAsync(
                CreateDownloadRequest(outputDir),
                progress: null,
                CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.Message.Should().Contain("Content-Type: text/html");
            Directory.GetFiles(outputDir, "*.mp4").Should().BeEmpty();
            Directory.GetFiles(outputDir, "*.part").Should().BeEmpty();
        }
        finally
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Fail_When_Ffprobe_Finds_No_Video_Stream()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-invalid-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = Encoding.UTF8.GetBytes("not-media"),
            DownloadMediaType = null
        };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) => Task.FromResult(
                new DramaSourceRouter.ProcessRunResult(1, "", "Invalid data found when processing input"));

            var result = await CreateRouter(httpClient, CreateLocalSettings(downloadMode: "compatible")).DownloadAsync(
                CreateDownloadRequest(outputDir),
                progress: null,
                CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.Message.Should().Contain("ffprobe 无法识别媒体");
            Directory.GetFiles(outputDir, "*.mp4").Should().BeEmpty();
            Directory.GetFiles(outputDir, "*.part").Should().BeEmpty();
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Replace_Invalid_Existing_Mp4_After_Redownload()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-invalid-existing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var finalPath = Path.Combine(outputDir, "第1集.mp4");
        await File.WriteAllTextAsync(finalPath, "<html>stale response</html>");
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = Encoding.UTF8.GetBytes("valid-video")
        };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var progress = new RecordingProgress();

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (startInfo, _) =>
            {
                var probedPath = startInfo.ArgumentList[^1];
                return Task.FromResult(string.Equals(probedPath, finalPath, StringComparison.OrdinalIgnoreCase)
                    ? new DramaSourceRouter.ProcessRunResult(1, "", "Invalid data")
                    : ProbeResult("h264"));
            };

            var result = await CreateRouter(httpClient, CreateLocalSettings()).DownloadAsync(
                CreateDownloadRequest(outputDir),
                progress,
                CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllText(finalPath).Should().Be("valid-video");
            progress.Messages.Should().Contain(message => message.Contains("无效的已有视频", StringComparison.Ordinal) &&
                                                          message.Contains("重新下载", StringComparison.Ordinal));
            handler.Requests.Should().Contain(request =>
                string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_ReplaceAll_Redownloads_Valid_Existing_Video()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-force-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var finalPath = Path.Combine(outputDir, "第1集.mp4");
        await File.WriteAllTextAsync(finalPath, "old-valid-video");
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = Encoding.UTF8.GetBytes("new-valid-video")
        };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var progress = new RecordingProgress();

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) => Task.FromResult(ProbeResult("h264"));

            var request = CreateDownloadRequest(outputDir) with
            {
                ExistingVideoPolicy = ExistingVideoPolicy.ReplaceAll
            };
            var result = await CreateRouter(httpClient, CreateLocalSettings()).DownloadAsync(
                request,
                progress,
                CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllText(finalPath).Should().Be("new-valid-video");
            progress.Messages.Should().NotContain(message =>
                message.Contains("强制重新下载", StringComparison.Ordinal));
            progress.Messages.Should().NotContain(message =>
                message.Contains("已存在，跳过", StringComparison.Ordinal));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_ReplaceAll_Preserves_Existing_Video_When_New_Download_Is_Invalid()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-force-preserve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var finalPath = Path.Combine(outputDir, "第1集.mp4");
        await File.WriteAllTextAsync(finalPath, "old-valid-video");
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = Encoding.UTF8.GetBytes("{\"error\":\"invalid media\"}")
        };
        using var httpClient = new HttpClient(handler);

        try
        {
            var request = CreateDownloadRequest(outputDir) with
            {
                ExistingVideoPolicy = ExistingVideoPolicy.ReplaceAll
            };
            var result = await CreateRouter(httpClient, CreateLocalSettings()).DownloadAsync(
                request,
                progress: null,
                CancellationToken.None);

            result.Ok.Should().BeFalse();
            File.ReadAllText(finalPath).Should().Be("old-valid-video");
            Directory.GetFiles(outputDir, "*.part").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, ExistingVideoPolicy.ReuseValid, false)]
    [InlineData(true, ExistingVideoPolicy.ReuseValid, true)]
    [InlineData(false, ExistingVideoPolicy.ReplaceInvalid, false)]
    [InlineData(false, ExistingVideoPolicy.ReplaceAll, false)]
    [InlineData(true, ExistingVideoPolicy.ReplaceAll, true)]
    public void Codec_Validation_Depends_Only_On_Source(
        bool sourceDefault,
        ExistingVideoPolicy policy,
        bool expected)
    {
        DramaSourceRouter.RequiresVideoEncodingValidation(sourceDefault, policy)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("hglocal", true)]
    [InlineData("HGLOCAL", true)]
    [InlineData("downloader", false)]
    [InlineData("hghigh", false)]
    [InlineData("mapleleaf", false)]
    [InlineData("pikachu", false)]
    [InlineData("hgnew", false)]
    [InlineData("", false)]
    public void Only_Local_Direct_Source_Enables_Codec_Validation(string source, bool expected)
    {
        DramaSourceRouter.ShouldValidateVideoEncodingForSource(source).Should().Be(expected);
    }

    private static DramaSourceSettings CreateLocalSettings(
        string downloadMode = "fast",
        string transcodeEngine = "auto",
        string fileSegments = "4") =>
        new()
        {
            DramaSourceChain = "hglocal",
            HongguoDownloadTimeoutSeconds = "10",
            HongguoEpisodeDownloadAttempts = "1",
            DownloadFileSegments = fileSegments,
            HongguoLocalBaseUrl = "https://local.example.com",
            HongguoLocalApiKey = "local-key",
            HongguoLocalDownloadMode = downloadMode,
            HongguoLocalTranscodeEngine = transcodeEngine
        };

    private static DramaDownloadRequest CreateDownloadRequest(string outputDir) =>
        new(
            ProjectDir: outputDir,
            OutputDir: outputDir,
            DisplayName: "test-drama",
            BookId: "hglocal:series-1",
            Episodes: "1",
            Quality: "1080P+",
            Concurrent: 1,
            EpisodeNumberMode: "source");

    private static DramaSourceRouter CreateRouter(HttpClient httpClient, DramaSourceSettings settings) =>
        new(
            httpClient,
            new TestDramaSettingsProvider(settings),
            new HongguoLocalApiService(httpClient),
            new HongguoNewApiService(httpClient),
            new HongguoDramaSearchService(httpClient),
            new HongguoDramaDownloader(httpClient),
            new HongguoMemoryReaderService());

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }

    private sealed class DownloaderInvalidVideoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/catalog/episodes", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json("""{"episodes":[{"vid":"episode-1","index":1,"title":"第1集"}]}"""));
            }

            if (path.EndsWith("/api/v1/catalog/video-url", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json("""{"url":"https://cdn.example/video.mp4","encrypt":false}"""));
            }

            if (string.Equals(request.RequestUri.Host, "cdn.example", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("mp4-container-with-unknown-video-codec"))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class LocalStreamRecordingHandler : HttpMessageHandler
    {
        public static readonly byte[] StreamBytes = Encoding.UTF8.GetBytes("hevc-video");

        public List<HttpRequestMessage> Requests { get; } = [];

        public byte[] DownloadBytes { get; init; } = StreamBytes;

        public string? DownloadMediaType { get; init; } = "video/mp4";

        public bool SupportsRanges { get; init; }

        public bool RejectOneDataRange { get; init; }

        public bool BlockDataRangesUntilCancelled { get; init; }

        public bool ReturnPartialForRequestsWithoutRange { get; init; }

        public bool ReturnMismatchedContentRangeOnce { get; init; }

        public int MismatchedContentRangeResponses => _mismatchedContentRange;

        public TaskCompletionSource<bool> DataRangeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _rejectedDataRange;
        private int _mismatchedContentRange;

        public IReadOnlyList<RangeItemHeaderValue> GetCdnRanges()
        {
            lock (Requests)
            {
                return Requests
                    .Where(request => string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(request => request.Headers.Range?.Ranges ?? [])
                    .ToArray();
            }
        }

        public IReadOnlyList<HttpRequestMessage> GetCdnRequests()
        {
            lock (Requests)
            {
                return Requests
                    .Where(request => string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
        }

        public IReadOnlyList<HttpRequestMessage> GetCdnDataRangeRequests()
        {
            lock (Requests)
            {
                return Requests
                    .Where(request =>
                    {
                        if (!string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        var range = request.Headers.Range?.Ranges.SingleOrDefault();
                        return range is not null && (range.From != 0 || range.To != 0);
                    })
                    .ToArray();
            }
        }

        public IReadOnlyList<HttpRequestMessage> GetCdnRequestsWithoutRange()
        {
            lock (Requests)
            {
                return Requests
                    .Where(request =>
                        string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase) &&
                        request.Headers.Range is null)
                    .ToArray();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/hongguo/episodes", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "episodes": [
                        { "index": 1, "title": "第1集", "vid": "video-1" }
                      ]
                    }
                    """));
            }

            if (path.EndsWith("/api/hongguo/video_url", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "url": "https://cdn.example.com/encrypted.mp4",
                      "encrypted_url": "https://cdn.example.com/encrypted.mp4",
                      "spade_a": "test-spade",
                      "encrypt": true
                    }
                    """));
            }

            if (string.Equals(request.RequestUri.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase))
            {
                var requestedRange = request.Headers.Range?.Ranges.SingleOrDefault();
                if (SupportsRanges && requestedRange is not null)
                {
                    var start = requestedRange.From ?? 0;
                    var end = requestedRange.To ?? DownloadBytes.LongLength - 1;
                    if (BlockDataRangesUntilCancelled && end > 0)
                    {
                        DataRangeStarted.TrySetResult(true);
                        return WaitUntilCancelledAsync(cancellationToken);
                    }

                    if (RejectOneDataRange &&
                        end > 0 &&
                        Interlocked.CompareExchange(ref _rejectedDataRange, 1, 0) == 0)
                    {
                        return Task.FromResult(CreateVideoResponse(HttpStatusCode.OK, DownloadBytes));
                    }

                    var count = checked((int)(end - start + 1));
                    var bytes = new byte[count];
                    Buffer.BlockCopy(DownloadBytes, checked((int)start), bytes, 0, count);
                    var partial = CreateVideoResponse(HttpStatusCode.PartialContent, bytes);
                    if (ReturnMismatchedContentRangeOnce &&
                        end > start &&
                        Interlocked.CompareExchange(ref _mismatchedContentRange, 1, 0) == 0)
                    {
                        partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end - 1, DownloadBytes.LongLength);
                    }
                    else
                    {
                        partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, DownloadBytes.LongLength);
                    }
                    return Task.FromResult(partial);
                }

                if (ReturnPartialForRequestsWithoutRange && requestedRange is null)
                {
                    var count = Math.Min(128 * 1024, DownloadBytes.Length);
                    var bytes = DownloadBytes[..count];
                    var partial = CreateVideoResponse(HttpStatusCode.PartialContent, bytes);
                    partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, count - 1, DownloadBytes.LongLength);
                    return Task.FromResult(partial);
                }

                return Task.FromResult(CreateVideoResponse(HttpStatusCode.OK, DownloadBytes));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private HttpResponseMessage CreateVideoResponse(HttpStatusCode statusCode, byte[] bytes)
        {
            var content = new ByteArrayContent(bytes);
                if (!string.IsNullOrWhiteSpace(DownloadMediaType))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue(DownloadMediaType);
                }

            var response = new HttpResponseMessage(statusCode) { Content = content };
            response.Headers.ETag = new EntityTagHeaderValue("\"video-v1\"");
            return response;
        }

        private static async Task<HttpResponseMessage> WaitUntilCancelledAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("等待取消的分块请求意外完成。");
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private static byte[] BuildLargeVideoBytes()
    {
        var bytes = new byte[(4 * 1024 * 1024) + 37];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private static byte[] BuildMp4WithDeclaredMdatSize(int declaredMdatSize, int actualPayloadSize)
    {
        var bytes = new byte[8 + 8 + 8 + actualPayloadSize];
        WriteBoxHeader(bytes.AsSpan(0, 8), 8, "ftyp"u8);
        WriteBoxHeader(bytes.AsSpan(8, 8), 8, "moov"u8);
        WriteBoxHeader(bytes.AsSpan(16, 8), declaredMdatSize, "mdat"u8);
        return bytes;
    }

    private static void WriteBoxHeader(Span<byte> target, int size, ReadOnlySpan<byte> type)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target[..4], checked((uint)size));
        type.CopyTo(target[4..8]);
    }

    private sealed class HgnewDownloadRecordingHandler(byte[]? videoBytes = null, string? mediaType = "video/mp4") : HttpMessageHandler
    {
        public static readonly byte[] VideoBytes = Encoding.UTF8.GetBytes("hgnew-source-video");

        private readonly byte[] _videoBytes = videoBytes ?? VideoBytes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/User/login", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(RestOk(new
                {
                    accessToken = "jwt-token",
                    expiresIn = 3600
                }, "登录成功")));
            }

            if (path.EndsWith("/api/ThirdParty/videolist", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(RestWrapped(new
                {
                    code = 200,
                    msg = "获取列表成功",
                    data = new object[]
                    {
                        new { title = "第01集", video_id = "video-1" }
                    }
                }, "获取视频列表成功")));
            }

            if (path.EndsWith("/api/ThirdParty/videoparse", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(RestWrapped(new
                {
                    code = 200,
                    msg = "解析成功",
                    url = "https://cdn.hgnew.example.com/video-1.mp4",
                    data = new { url = "https://cdn.hgnew.example.com/video-1.mp4" }
                }, "视频解析成功")));
            }

            if (string.Equals(request.RequestUri.Host, "cdn.hgnew.example.com", StringComparison.OrdinalIgnoreCase))
            {
                var content = new ByteArrayContent(_videoBytes);
                if (!string.IsNullOrWhiteSpace(mediaType))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string RestOk(object data, string message) =>
            JsonSerializer.Serialize(new { success = true, message, data });

        private static string RestWrapped(object inner, string message) =>
            JsonSerializer.Serialize(new
            {
                success = true,
                message,
                data = new
                {
                    success = true,
                    message,
                    rawData = JsonSerializer.Serialize(inner)
                }
            });

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private static DramaSourceRouter.ProcessRunResult ProbeResult(string codec) =>
        new(0, $$"""
            {
              "streams": [
                { "codec_type": "video", "codec_name": "{{codec}}" }
              ]
            }
            """, "");

    private sealed class TestDramaSettingsProvider(DramaSourceSettings settings) : IDramaSettingsProvider
    {
        public DramaSourceSettings Get() => settings;

        public void SavePikachuDeviceId(string deviceId)
        {
        }
    }
}
