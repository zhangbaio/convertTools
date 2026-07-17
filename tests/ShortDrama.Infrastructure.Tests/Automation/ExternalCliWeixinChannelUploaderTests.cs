using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class ExternalCliWeixinChannelUploaderTests
{
    [Fact]
    public async Task UploadAsync_Should_Use_Project_Dir_And_Config_Name_When_No_Config_Path_Is_Provided()
    {
        using var fixture = new TestWorkspace();
        var toolDir = fixture.CreateDirectory("tool");
        var scriptPath = fixture.CreateFile(
            "tool/main.py",
            """
            import pathlib, sys

            args = sys.argv[1:]
            if "--project-dir" not in args:
                print("missing-project-dir", flush=True)
                sys.exit(7)
            if "--config-name" not in args:
                print("missing-config-name", flush=True)
                sys.exit(8)

            project_dir = pathlib.Path(args[args.index("--project-dir") + 1])
            config_name = args[args.index("--config-name") + 1]
            print(f"project-dir={project_dir.name}", flush=True)
            print(f"config-name={config_name}", flush=True)
            sys.exit(0)
            """);
        var projectDir = fixture.CreateDirectory("project");
        fixture.CreateFile("project/custom-upload.json", "{}");
        var uploader = new ExternalCliWeixinChannelUploader(
            new FakeExternalWeixinCliLocator(toolDir, scriptPath),
            new WorkflowInteractionService(),
            NullLogger<ExternalCliWeixinChannelUploader>.Instance);

        var result = await uploader.UploadAsync(
            new WeixinUploadRequest(
                ProjectKey: "project-2",
                ProjectDir: projectDir,
                DisplayName: "测试项目2",
                ConfigPath: null,
                ConfigName: "custom-upload.json"),
            progress: null,
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.ConfigPath.Should().Be(Path.Combine(projectDir, "custom-upload.json"));
        result.Message.Should().Be("config-name=custom-upload.json");
    }

    [Fact]
    public async Task UploadAsync_Should_Use_Config_Path_When_Config_Path_Is_Provided()
    {
        using var fixture = new TestWorkspace();
        var toolDir = fixture.CreateDirectory("tool");
        var scriptPath = fixture.CreateFile(
            "tool/main.py",
            """
            import sys

            args = sys.argv[1:]
            if "--config" not in args:
                print("missing-config", flush=True)
                sys.exit(7)

            config_path = args[args.index("--config") + 1]
            print(f"config-path={config_path}", flush=True)
            sys.exit(0)
            """);
        var projectDir = fixture.CreateDirectory("project");
        var configPath = fixture.CreateFile("project/override.json", "{}");
        var uploader = new ExternalCliWeixinChannelUploader(
            new FakeExternalWeixinCliLocator(toolDir, scriptPath),
            new WorkflowInteractionService(),
            NullLogger<ExternalCliWeixinChannelUploader>.Instance);

        var result = await uploader.UploadAsync(
            new WeixinUploadRequest(
                ProjectKey: "project-1",
                ProjectDir: projectDir,
                DisplayName: "测试项目",
                ConfigPath: configPath,
                ConfigName: null),
            progress: null,
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.ConfigPath.Should().Be(Path.GetFullPath(configPath));
        result.Message.Should().Be($"config-path={Path.GetFullPath(configPath)}");
    }

    private sealed class FakeExternalWeixinCliLocator : IExternalWeixinCliLocator
    {
        private readonly string _toolDirectory;
        private readonly string _scriptPath;

        public FakeExternalWeixinCliLocator(string toolDirectory, string scriptPath)
        {
            _toolDirectory = toolDirectory;
            _scriptPath = scriptPath;
        }

        public Task<ExternalWeixinCliCommand> ResolveAsync(CancellationToken cancellationToken)
        {
            var pythonFileName = OperatingSystem.IsWindows() ? "python" : "python3";
            return Task.FromResult(new ExternalWeixinCliCommand(
                pythonFileName,
                [],
                _toolDirectory,
                _scriptPath));
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"external-weixin-cli-tests-{Guid.NewGuid():N}");

        public TestWorkspace()
        {
            Directory.CreateDirectory(RootPath);
        }

        public string CreateDirectory(string relativePath)
        {
            var path = Path.Combine(RootPath, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(string relativePath, string content)
        {
            var path = Path.Combine(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }
}
