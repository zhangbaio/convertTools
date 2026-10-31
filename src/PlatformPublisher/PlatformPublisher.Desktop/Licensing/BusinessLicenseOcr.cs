using System.Diagnostics;
using System.Text;

namespace PlatformPublisher.Desktop.Licensing;

public static class BusinessLicenseOcr
{
    public static async Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
    {
        var script = Path.Combine(Path.GetTempPath(), "yunfan-license-ocr.ps1");
        await File.WriteAllTextAsync(script, Script, Encoding.UTF8, cancellationToken);
        var start = new ProcessStartInfo
        {
            FileName = "powershell",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(imagePath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Windows 文字识别。");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows 文字识别失败。" : error.Trim());
        return output;
    }

    private const string Script = """
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
        $OutputEncoding = [Console]::OutputEncoding
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await([object]$op, [type]$type) {
          $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
          $task.Wait()
          $task.Result
        }
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Media.Ocr, ContentType=WindowsRuntime]
        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($args[0])) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($null -eq $engine) { throw '未安装中文文字识别语言。请在 Windows 设置中添加中文（简体）语言包。' }
        $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        foreach ($line in $result.Lines) { Write-Output $line.Text }
        """;
}
