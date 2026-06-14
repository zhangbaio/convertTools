using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;

namespace ShortDrama.Infrastructure.Rendering;

public sealed class LibreOfficeDocumentRenderService : IDocumentRenderService
{
    private const string LibreOfficePathEnv = "SHORTDRAMA_LIBREOFFICE_PATH";
    private const string PdfToPpmPathEnv = "SHORTDRAMA_PDFTOPPM_PATH";
    private const string MagickPathEnv = "SHORTDRAMA_MAGICK_PATH";

    private readonly IExternalProcessRunner _processRunner;
    private readonly ILogger<LibreOfficeDocumentRenderService> _logger;

    public LibreOfficeDocumentRenderService(
        IExternalProcessRunner processRunner,
        ILogger<LibreOfficeDocumentRenderService> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<string> ConvertDocxToPdfAsync(
        string docxPath,
        string outputDir,
        CancellationToken cancellationToken)
    {
        await EnsureBundledFontsInstalledAsync(cancellationToken);

        if (!File.Exists(docxPath))
        {
            throw new FileNotFoundException($"未找到 docx 文件: {docxPath}", docxPath);
        }

        Directory.CreateDirectory(outputDir);

        var soffice = ResolveSofficeBinary();
        var result = await _processRunner.RunAsync(
            soffice,
            new[]
            {
                "--headless",
                "--convert-to", "pdf",
                "--outdir", outputDir,
                docxPath
            },
            outputDir,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"LibreOffice 转 PDF 失败，退出码 {result.ExitCode}。stderr: {result.StandardError}");
        }

        var expectedPdfPath = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(docxPath)}.pdf");

        if (!File.Exists(expectedPdfPath))
        {
            throw new InvalidOperationException($"LibreOffice 执行成功，但未找到输出 PDF: {expectedPdfPath}");
        }

        _logger.LogInformation("Converted docx to pdf: {Pdf}", expectedPdfPath);
        return expectedPdfPath;
    }

    private async Task EnsureBundledFontsInstalledAsync(CancellationToken cancellationToken)
    {
        string? fontsSource = null;
        foreach (var root in EnumerateSearchRoots())
        {
            var candidate = Path.Combine(root, "tools", "fonts");
            if (Directory.Exists(candidate))
            {
                fontsSource = candidate;
                break;
            }
        }

        if (fontsSource is null)
        {
            _logger.LogWarning("Bundled fonts directory tools/fonts not found. Skipping installation.");
            return;
        }

        string? fontsDestination = null;
        if (OperatingSystem.IsMacOS())
        {
            fontsDestination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Library",
                "Fonts");
        }
        else if (OperatingSystem.IsLinux())
        {
            fontsDestination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "fonts");
        }
        else if (OperatingSystem.IsWindows())
        {
            fontsDestination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Windows",
                "Fonts");
        }

        if (string.IsNullOrWhiteSpace(fontsDestination))
        {
            _logger.LogWarning("Unsupported OS for installing bundled fonts.");
            return;
        }

        Directory.CreateDirectory(fontsDestination);

        var installedCount = 0;
        foreach (var fontPath in Directory.EnumerateFiles(fontsSource, "*.ttf", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destinationPath = Path.Combine(fontsDestination, Path.GetFileName(fontPath));
            if (File.Exists(destinationPath))
            {
                continue;
            }

            File.Copy(fontPath, destinationPath);
            installedCount++;
        }

        _logger.LogInformation(
            "Installed {Count} bundled font(s) into {Destination}",
            installedCount,
            fontsDestination);

        if (installedCount > 0 && OperatingSystem.IsLinux())
        {
            var fcCacheBinary = TryResolveBinary("fc-cache") ?? "fc-cache";

            try
            {
                var result = await _processRunner.RunAsync(
                    fcCacheBinary,
                    new[] { "-f" },
                    fontsDestination,
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "fc-cache exited with {ExitCode} after installing fonts: {Error}",
                        result.ExitCode,
                        result.StandardError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to run fc-cache after installing fonts.");
            }
        }
    }

    public async Task<string> ConvertPdfFirstPageToPngAsync(
        string pdfPath,
        string outputPngPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException($"未找到 pdf 文件: {pdfPath}", pdfPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);

        if (await TryConvertWithPdfToPpmAsync(pdfPath, outputPngPath, cancellationToken))
        {
            _logger.LogInformation("Converted pdf to png with pdftoppm: {Png}", outputPngPath);
            return outputPngPath;
        }

        if (await TryConvertWithImageMagickAsync(pdfPath, outputPngPath, cancellationToken))
        {
            _logger.LogInformation("Converted pdf to png with magick: {Png}", outputPngPath);
            return outputPngPath;
        }

        throw new InvalidOperationException("无法将 PDF 转成 PNG。未找到可用渲染器，请安装 pdftoppm 或 ImageMagick。");
    }

    private async Task<bool> TryConvertWithPdfToPpmAsync(
        string pdfPath,
        string outputPngPath,
        CancellationToken cancellationToken)
    {
        var pdftoppm = ResolveOptionalBinary(
            PdfToPpmPathEnv,
            "pdftoppm",
            GetBundledPdftoppmCandidates());
        if (pdftoppm is null)
        {
            return false;
        }

        var prefix = Path.Combine(
            Path.GetDirectoryName(outputPngPath)!,
            Path.GetFileNameWithoutExtension(outputPngPath));

        var result = await _processRunner.RunAsync(
            pdftoppm,
            new[]
            {
                "-png",
                "-f", "1",
                "-singlefile",
                pdfPath,
                prefix
            },
            Path.GetDirectoryName(outputPngPath),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pdftoppm 转 PNG 失败，退出码 {result.ExitCode}。stderr: {result.StandardError}");
        }

        return File.Exists(outputPngPath);
    }

    private async Task<bool> TryConvertWithImageMagickAsync(
        string pdfPath,
        string outputPngPath,
        CancellationToken cancellationToken)
    {
        var magick = ResolveOptionalBinary(
            MagickPathEnv,
            "magick",
            GetBundledMagickCandidates());
        if (magick is null)
        {
            return false;
        }

        var firstPage = $"{pdfPath}[0]";

        var result = await _processRunner.RunAsync(
            magick,
            new[]
            {
                "-density", "180",
                firstPage,
                "-quality", "100",
                outputPngPath
            },
            Path.GetDirectoryName(outputPngPath),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ImageMagick 转 PNG 失败，退出码 {result.ExitCode}。stderr: {result.StandardError}");
        }

        return File.Exists(outputPngPath);
    }

    private static string ResolveSofficeBinary()
    {
        var resolved = ResolveOptionalBinary(
            LibreOfficePathEnv,
            "soffice",
            GetBundledSofficeCandidates(),
            GetSystemSofficeCandidates());
        if (resolved is not null)
        {
            return resolved;
        }

        throw new InvalidOperationException(
            "未找到 LibreOffice 可执行文件 soffice。请设置环境变量 SHORTDRAMA_LIBREOFFICE_PATH，" +
            "或将可执行文件放入 tools/<os-arch>/libreoffice 目录，或确保 soffice 在 PATH 中。");
    }

    private static string? ResolveOptionalBinary(
        string envVarName,
        string binaryName,
        params IEnumerable<string>[] candidateGroups)
    {
        var envPath = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        foreach (var candidate in candidateGroups.SelectMany(group => group))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return TryResolveBinary(binaryName);
    }

    private static IEnumerable<string> GetBundledSofficeCandidates()
    {
        var rid = GetOsArchKey();
        foreach (var root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, "tools", rid, "libreoffice", ExecutableName("soffice"));
            yield return Path.Combine(root, "tools", rid, "libreoffice", "program", ExecutableName("soffice"));
            yield return Path.Combine(root, "tools", rid, "LibreOffice", "program", ExecutableName("soffice"));

            if (OperatingSystem.IsMacOS())
            {
                yield return Path.Combine(root, "tools", rid, "LibreOffice.app", "Contents", "MacOS", "soffice");
                yield return Path.Combine(root, "tools", rid, "libreoffice", "LibreOffice.app", "Contents", "MacOS", "soffice");
            }
        }
    }

    private static IEnumerable<string> GetSystemSofficeCandidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/LibreOffice.app/Contents/MacOS/soffice";
        }
    }

    private static IEnumerable<string> GetBundledPdftoppmCandidates()
    {
        var rid = GetOsArchKey();
        foreach (var root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, "tools", rid, "poppler", ExecutableName("pdftoppm"));
            yield return Path.Combine(root, "tools", rid, "pdftoppm", ExecutableName("pdftoppm"));
            yield return Path.Combine(root, "tools", rid, "poppler", "bin", ExecutableName("pdftoppm"));
        }
    }

    private static IEnumerable<string> GetBundledMagickCandidates()
    {
        var rid = GetOsArchKey();
        foreach (var root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, "tools", rid, "imagemagick", ExecutableName("magick"));
            yield return Path.Combine(root, "tools", rid, "magick", ExecutableName("magick"));
            yield return Path.Combine(root, "tools", rid, "imagemagick", "bin", ExecutableName("magick"));
        }
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }

                current = current.Parent;
            }
        }
    }

    private static string GetOsArchKey()
    {
        var os = OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsWindows() ? "windows"
            : "linux";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return $"{os}-{arch}";
    }

    private static string ExecutableName(string name)
    {
        return OperatingSystem.IsWindows() ? $"{name}.exe" : name;
    }

    private static string? TryResolveBinary(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", string.Empty }
            : new[] { string.Empty };

        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
