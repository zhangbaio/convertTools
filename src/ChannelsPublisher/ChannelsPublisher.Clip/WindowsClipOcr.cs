using System.Text.Json;

namespace ChannelsPublisher.Clip;

internal static class WindowsClipOcr
{
    public static async Task<SubtitleObservation[]> ReadAsync(string folder, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var (code, output, _) = await Ffmpeg.RunCaptureAsync("powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", RecognizeScript], timeout.Token, folder);
        if (code != 0)
            throw new InvalidOperationException("Windows OCR 无法识别，请检查系统 OCR 语言包。");
        return JsonSerializer.Deserialize<SubtitleObservation[]>(output) ?? [];
    }

    private const string RecognizeScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Media.Ocr, ContentType=WindowsRuntime]
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        })[0]
        function Await([object]$operation, [type]$type) {
            $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
            $task.GetAwaiter().GetResult()
        }
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($null -eq $engine) { throw 'No Windows OCR language is available.' }
        $boxes = foreach ($image in Get-ChildItem -LiteralPath . -File -Filter 'frame-*') {
            if ($image.BaseName -notmatch '^frame-(\d+)$') { continue }
            $frame = [int]$Matches[1]
            $stream = $null
            $bitmap = $null
            try {
                $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($image.FullName)) ([Windows.Storage.StorageFile])
                $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
                $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
                $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
                $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                foreach ($line in $result.Lines) {
                    if ($line.Text -notmatch '[\p{L}\p{N}]' -or $line.Words.Count -eq 0) { continue }
                    $rects = $line.Words | ForEach-Object { $_.BoundingRect }
                    $left = ($rects | Measure-Object X -Minimum).Minimum
                    $top = ($rects | Measure-Object Y -Minimum).Minimum
                    $right = ($rects | ForEach-Object { $_.X + $_.Width } | Measure-Object -Maximum).Maximum
                    $bottom = ($rects | ForEach-Object { $_.Y + $_.Height } | Measure-Object -Maximum).Maximum
                    [pscustomobject]@{ Frame=$frame; Text=$line.Text; X=$left/$bitmap.PixelWidth; Y=$top/$bitmap.PixelHeight;
                        Width=($right-$left)/$bitmap.PixelWidth; Height=($bottom-$top)/$bitmap.PixelHeight }
                }
            }
            finally {
                if ($null -ne $bitmap) { $bitmap.Dispose() }
                if ($null -ne $stream) { $stream.Dispose() }
            }
        }
        ConvertTo-Json -InputObject @($boxes) -Compress
        """;
}
