using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace TikTokPublisher.Core.Services;

public interface ITikTokProofMaterialPdfRenderer
{
    string Name { get; }

    Task RenderAsync(
        string docxPath,
        string outputPdfPath,
        TikTokProofMaterialPdfRenderOptions options,
        CancellationToken cancellationToken);
}

public sealed class TikTokProofMaterialPdfRenderService
{
    public const long MaxPlatformPdfBytes = 10L * 1024 * 1024;
    private readonly ITikTokProofMaterialPdfRenderer _wpsRenderer;
    private readonly ITikTokProofMaterialPdfRenderer _libreOfficeRenderer;

    public TikTokProofMaterialPdfRenderService(
        ITikTokProofMaterialPdfRenderer? wpsRenderer = null,
        ITikTokProofMaterialPdfRenderer? libreOfficeRenderer = null)
    {
        _wpsRenderer = wpsRenderer ?? new WpsProofMaterialPdfRenderer();
        _libreOfficeRenderer = libreOfficeRenderer ?? new LibreOfficeProofMaterialPdfRenderer();
    }

    public async Task<TikTokProofMaterialPdfRenderResult> RenderAsync(
        string docxPath,
        string outputPdfPath,
        TikTokProofMaterialPdfRenderOptions? options = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TikTokProofMaterialPdfRenderOptions();
        ValidateInput(docxPath, outputPdfPath);

        var fullOutputPath = Path.GetFullPath(outputPdfPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("证明材料 PDF 输出目录无效。");
        Directory.CreateDirectory(outputDirectory);

        var renderers = options.PreferredRenderer == TikTokProofMaterialPdfRendererPreference.Wps
            ? new[] { _wpsRenderer, _libreOfficeRenderer }
            : new[] { _libreOfficeRenderer };
        var errors = new List<string>();

        foreach (var renderer in renderers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPdfPath = Path.Combine(
                outputDirectory,
                $".{Path.GetFileNameWithoutExtension(fullOutputPath)}.{Guid.NewGuid():N}.tmp.pdf");
            TryDelete(temporaryPdfPath);

            try
            {
                log?.Invoke($"正在使用 {renderer.Name} 生成证明材料 PDF。");
                await renderer.RenderAsync(
                    Path.GetFullPath(docxPath),
                    temporaryPdfPath,
                    NormalizeOptions(options),
                    cancellationToken).ConfigureAwait(false);
                ValidatePdf(temporaryPdfPath);
                File.Move(temporaryPdfPath, fullOutputPath, overwrite: true);
                return new TikTokProofMaterialPdfRenderResult(fullOutputPath, renderer.Name);
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporaryPdfPath);
                throw;
            }
            catch (Exception exception)
            {
                TryDelete(temporaryPdfPath);
                errors.Add($"{renderer.Name}: {ShortMessage(exception)}");
                if (!ReferenceEquals(renderer, renderers[^1]))
                {
                    log?.Invoke($"{renderer.Name} 生成失败，正在使用 LibreOffice 兜底：{ShortMessage(exception)}");
                }
            }
        }

        throw new InvalidOperationException(
            $"证明材料 PDF 生成失败：{string.Join("；", errors)}");
    }

    public static void ValidatePdf(string pdfPath)
    {
        if (!File.Exists(pdfPath))
        {
            throw new InvalidDataException($"未生成证明材料 PDF：{pdfPath}");
        }

        using var stream = File.OpenRead(pdfPath);
        if (stream.Length < 5)
        {
            throw new InvalidDataException("生成的证明材料 PDF 文件为空或不完整。");
        }

        if (stream.Length > MaxPlatformPdfBytes)
        {
            throw new InvalidDataException(
                $"生成的证明材料 PDF 超过 TikTok 平台 10 MB 限制：{stream.Length / 1024d / 1024d:F2} MB。");
        }

        Span<byte> header = stackalloc byte[5];
        if (stream.Read(header) != header.Length ||
            !header.SequenceEqual("%PDF-"u8))
        {
            throw new InvalidDataException("生成的证明材料文件不是有效 PDF（缺少 %PDF- 文件头）。");
        }
    }

    private static void ValidateInput(string docxPath, string outputPdfPath)
    {
        if (string.IsNullOrWhiteSpace(docxPath) || !File.Exists(docxPath))
        {
            throw new FileNotFoundException("未找到待渲染的证明材料 DOCX。", docxPath);
        }

        if (string.IsNullOrWhiteSpace(outputPdfPath))
        {
            throw new ArgumentException("证明材料 PDF 输出路径不能为空。", nameof(outputPdfPath));
        }
    }

    private static TikTokProofMaterialPdfRenderOptions NormalizeOptions(
        TikTokProofMaterialPdfRenderOptions options) =>
        options with
        {
            Timeout = options.Timeout <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(180)
                : options.Timeout,
        };

    private static string ShortMessage(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : string.Join(' ', exception.Message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return message.Length <= 500 ? message : message[..500];
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for renderer output.
        }
    }
}

public sealed class WpsProofMaterialPdfRenderer : ITikTokProofMaterialPdfRenderer
{
    private static readonly SemaphoreSlim RenderLock = new(1, 1);

    public string Name => "WPS";

    public async Task RenderAsync(
        string docxPath,
        string outputPdfPath,
        TikTokProofMaterialPdfRenderOptions options,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WPS PDF 渲染仅支持 Windows。");
        }

        await RenderLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TikTokProofMaterialPdfRenderService.TryDelete(outputPdfPath);
            var wpsPath = WpsExecutableResolver.Resolve(options.WpsExecutablePath) ?? string.Empty;
            var script = BuildPowerShellScript(docxPath, outputPdfPath, wpsPath);
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolvePowerShellExecutable(),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(docxPath)) ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var argument in new[]
                     {
                         "-NoLogo",
                         "-NoProfile",
                         "-NonInteractive",
                         "-ExecutionPolicy",
                         "Bypass",
                         "-EncodedCommand",
                         encodedScript,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            var result = await ProofMaterialProcessRunner.RunAsync(
                startInfo,
                options.Timeout,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"WPS 转 PDF 失败（退出码 {result.ExitCode}）：{CleanPowerShellOutput(result.StandardError, result.StandardOutput)}");
            }

            if (!File.Exists(outputPdfPath))
            {
                throw new InvalidOperationException("WPS 执行完成，但没有生成 PDF 文件。");
            }
        }
        catch
        {
            TikTokProofMaterialPdfRenderService.TryDelete(outputPdfPath);
            throw;
        }
        finally
        {
            RenderLock.Release();
        }
    }

    internal static string BuildPowerShellScript(string docxPath, string outputPdfPath, string? wpsPath)
    {
        var docx = EscapePowerShellSingleQuoted(Path.GetFullPath(docxPath));
        var pdf = EscapePowerShellSingleQuoted(Path.GetFullPath(outputPdfPath));
        var executable = EscapePowerShellSingleQuoted(wpsPath?.Trim() ?? string.Empty);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
            $docPath = '{{docx}}'
            $pdfPath = '{{pdf}}'
            $wpsPath = '{{executable}}'
            $app = $null
            $doc = $null
            $progIds = @('KWPS.Application', 'wps.Application')
            $comErrors = [System.Collections.Generic.List[string]]::new()

            function New-WpsApplication {
                foreach ($progId in $progIds) {
                    try {
                        $candidate = New-Object -ComObject $progId
                        if ($candidate) { return $candidate }
                    } catch {
                        $message = $_.Exception.Message
                        $entry = "${progId}: $message"
                        if ($message -and -not $comErrors.Contains($entry)) { $comErrors.Add($entry) }
                    }
                }
                return $null
            }

            try {
                $app = New-WpsApplication
                if (-not $app -and $wpsPath -and (Test-Path -LiteralPath $wpsPath)) {
                    Start-Process -FilePath $wpsPath -WindowStyle Hidden | Out-Null
                    # Cold start, first-run initialization and post-update startup can exceed 10 seconds.
                    for ($i = 0; $i -lt 120; $i++) {
                        Start-Sleep -Milliseconds 500
                        $app = New-WpsApplication
                        if ($app) { break }
                    }
                }

                if (-not $app) {
                    $detail = if ($comErrors.Count -gt 0) { $comErrors -join '; ' } else { '未返回 COM 对象' }
                    throw "等待 WPS 自动化组件就绪超时（60 秒）：$detail。请先手动打开 WPS 文字并完成首次启动、用户协议或升级提示后重试。"
                }

                try { $app.Visible = $false } catch {}
                try { $app.DisplayAlerts = 0 } catch {}
                try { $app.AutomationSecurity = 3 } catch {}
                $documents = $app.Documents
                if (-not $documents) { throw 'WPS Documents 集合不可用。' }
                $doc = $documents.Open($docPath, $false, $true)
                if (-not $doc) { throw 'WPS 未能以只读方式打开证明材料 DOCX。' }

                $wdExportFormatPDF = 17
                $exported = $false
                $exportErrors = [System.Collections.Generic.List[string]]::new()
                try {
                    $doc.ExportAsFixedFormat($pdfPath, $wdExportFormatPDF)
                    $exported = $true
                } catch {
                    $exportErrors.Add("ExportAsFixedFormat: $($_.Exception.Message)")
                }
                if (-not $exported) {
                    try {
                        $doc.SaveAs2($pdfPath, $wdExportFormatPDF)
                        $exported = $true
                    } catch {
                        $exportErrors.Add("SaveAs2: $($_.Exception.Message)")
                    }
                }
                if (-not $exported) {
                    try {
                        $doc.SaveAs($pdfPath, $wdExportFormatPDF)
                        $exported = $true
                    } catch {
                        $exportErrors.Add("SaveAs: $($_.Exception.Message)")
                    }
                }
                if (-not $exported) {
                    throw "WPS PDF 导出接口全部失败：$($exportErrors -join '; ')"
                }
            } finally {
                if ($doc) {
                    try { $doc.Close(0) } catch {}
                    try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($doc) } catch {}
                }
                if ($app) {
                    try { $app.Quit() } catch {}
                    try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($app) } catch {}
                }
                [GC]::Collect()
                [GC]::WaitForPendingFinalizers()
            }
            """;
    }

    private static string EscapePowerShellSingleQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string ResolvePowerShellExecutable()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemPowerShell = string.IsNullOrWhiteSpace(systemDirectory)
            ? string.Empty
            : Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(systemPowerShell)
            ? systemPowerShell
            : ProofMaterialExecutableResolver.FindOnPath("powershell.exe") ?? "powershell.exe";
    }

    private static string CleanPowerShellOutput(params string[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var cleaned = value.Replace("#< CLIXML", string.Empty, StringComparison.OrdinalIgnoreCase);
            cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return cleaned.Length <= 1000 ? cleaned : cleaned[..1000];
        }

        return "无错误输出";
    }
}

public sealed class LibreOfficeProofMaterialPdfRenderer : ITikTokProofMaterialPdfRenderer
{
    public string Name => "LibreOffice";

    public async Task RenderAsync(
        string docxPath,
        string outputPdfPath,
        TikTokProofMaterialPdfRenderOptions options,
        CancellationToken cancellationToken)
    {
        var soffice = LibreOfficeExecutableResolver.Resolve(options.LibreOfficeExecutablePath)
            ?? throw new FileNotFoundException("未找到 LibreOffice soffice，无法执行 PDF 兜底渲染。");
        var renderDirectory = Path.Combine(Path.GetTempPath(), "TikTokPublisher", "proof-material-render", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(renderDirectory, "output");
        var profileDirectory = Path.Combine(renderDirectory, "profile");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(profileDirectory);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = soffice,
                WorkingDirectory = renderDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var argument in new[]
                     {
                         $"-env:UserInstallation={new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri}",
                         "--headless",
                         "--convert-to",
                         "pdf",
                         "--outdir",
                         outputDirectory,
                         Path.GetFullPath(docxPath),
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            var result = await ProofMaterialProcessRunner.RunAsync(
                startInfo,
                options.Timeout,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"LibreOffice 转 PDF 失败（退出码 {result.ExitCode}）：{result.StandardError.Trim()}");
            }

            var generatedPdf = Path.Combine(
                outputDirectory,
                $"{Path.GetFileNameWithoutExtension(docxPath)}.pdf");
            if (!File.Exists(generatedPdf))
            {
                throw new InvalidOperationException(
                    $"LibreOffice 执行完成，但没有生成 PDF：{result.StandardOutput.Trim()}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPdfPath))!);
            File.Move(generatedPdf, outputPdfPath, overwrite: true);
        }
        catch
        {
            TikTokProofMaterialPdfRenderService.TryDelete(outputPdfPath);
            throw;
        }
        finally
        {
            TikTokProofMaterialDocumentBuilder.TryDeleteDirectory(renderDirectory);
        }
    }
}

public static class WpsExecutableResolver
{
    public static string? Resolve(string? configuredPath = null)
    {
        var configured = ProofMaterialExecutableResolver.NormalizeCandidate(configuredPath);
        if (configured is not null)
        {
            return configured;
        }

        var environmentPath = ProofMaterialExecutableResolver.NormalizeCandidate(
            Environment.GetEnvironmentVariable("SHORTDRAMA_WPS_PATH"));
        if (environmentPath is not null)
        {
            return environmentPath;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var registryCandidate in EnumerateRegistryCandidates())
            {
                var resolved = ProofMaterialExecutableResolver.NormalizeCandidate(registryCandidate);
                if (resolved is not null)
                {
                    return resolved;
                }
            }

            foreach (var localCandidate in EnumerateLocalAppDataCandidates())
            {
                if (File.Exists(localCandidate))
                {
                    return Path.GetFullPath(localCandidate);
                }
            }
        }

        foreach (var command in new[] { "wps.exe", "kwps.exe", "ksolaunch.exe", "wps", "kwps", "ksolaunch" })
        {
            var resolved = ProofMaterialExecutableResolver.FindOnPath(command);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string?> EnumerateRegistryCandidates()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var executable in new[] { "wps.exe", "kwps.exe" })
                {
                    RegistryKey? baseKey = null;
                    RegistryKey? appKey = null;
                    try
                    {
                        baseKey = RegistryKey.OpenBaseKey(hive, view);
                        appKey = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
                        yield return appKey?.GetValue(null)?.ToString();
                    }
                    finally
                    {
                        appKey?.Dispose();
                        baseKey?.Dispose();
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateLocalAppDataCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        var root = Path.Combine(localAppData, "Kingsoft", "WPS Office");
        foreach (var executable in new[] { "wps.exe", "kwps.exe", "ksolaunch.exe" })
        {
            yield return Path.Combine(root, "office6", executable);
            yield return Path.Combine(root, executable);
        }

        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> versionDirectories;
        try
        {
            versionDirectories = Directory.EnumerateDirectories(root)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var directory in versionDirectories)
        {
            foreach (var executable in new[] { "wps.exe", "kwps.exe", "ksolaunch.exe" })
            {
                yield return Path.Combine(directory, "office6", executable);
                yield return Path.Combine(directory, executable);
            }
        }
    }
}

public static class LibreOfficeExecutableResolver
{
    public static string? Resolve(string? configuredPath = null)
    {
        foreach (var candidate in new[]
                 {
                     configuredPath,
                     Environment.GetEnvironmentVariable("SHORTDRAMA_LIBREOFFICE_PATH"),
                     Environment.GetEnvironmentVariable("LIBREOFFICE_PATH"),
                 })
        {
            var resolved = ProofMaterialExecutableResolver.NormalizeCandidate(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        foreach (var root in EnumerateSearchRoots())
        {
            foreach (var relativePath in new[]
                     {
                         Path.Combine("tools", "windows-x64", "libreoffice", "program", "soffice.exe"),
                         Path.Combine("tools", "windows-x64", "libreoffice", "soffice.exe"),
                         Path.Combine("tools", "windows-x86", "libreoffice", "program", "soffice.exe"),
                     })
            {
                var candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var programFiles in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                var candidate = Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return ProofMaterialExecutableResolver.FindOnPath(OperatingSystem.IsWindows() ? "soffice.exe" : "soffice");
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
}

internal static class ProofMaterialExecutableResolver
{
    internal static string? NormalizeCandidate(string? candidate)
    {
        var value = candidate?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (File.Exists(value))
        {
            return Path.GetFullPath(value);
        }

        return Path.IsPathRooted(value) ? null : FindOnPath(value);
    }

    internal static string? FindOnPath(string executable)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), executable);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}

internal static class ProofMaterialProcessRunner
{
    internal static async Task<ProofMaterialProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动外部进程：{startInfo.FileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(
            timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(180) : timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            throw new TimeoutException($"外部进程执行超时（{Math.Ceiling(timeout.TotalSeconds)} 秒）：{startInfo.FileName}");
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            throw;
        }

        return new ProofMaterialProcessResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort; the caller still observes cancellation/timeout.
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Do not mask the original timeout/cancellation.
        }
    }
}

internal sealed record ProofMaterialProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
