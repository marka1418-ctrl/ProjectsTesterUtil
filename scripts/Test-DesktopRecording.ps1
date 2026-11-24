[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$FfmpegPath,
    [ValidateRange(3, 15)]
    [int]$DurationSeconds = 5,
    [ValidateRange(0, 16)]
    [int]$DxgiOutputIndex = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertTo-ProcessArgument([string]$Value) {
    # Windows command-line quoting; this string is passed directly to CreateProcess,
    # never to a shell. Preserve quotes and trailing backslashes in paths.
    if ($Value.IndexOf([char]0) -ge 0) { throw 'An argument contains a NUL character.' }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-BoundedFFmpeg {
    param([string]$Executable, [string[]]$ArgumentList, [string]$LogPrefix, [int]$TimeoutSeconds)
    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $Executable
    $process.StartInfo.Arguments = ($ArgumentList | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $result = [ordered]@{ ExitCode = $null; TimedOut = $false; Error = $null; Output = '' }
    $started = $false
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $process.Start() | Out-Null
        $started = $true
        # Drain both pipes concurrently: FFmpeg normally writes its logs to stderr.
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $result.TimedOut = $true
            $process.Kill()
            if (-not $process.WaitForExit(5000)) { throw 'FFmpeg did not stop after the diagnostic timeout.' }
        }
        $result.ExitCode = $process.ExitCode
        if (-not $outTask.Wait(5000) -or -not $errTask.Wait(5000)) {
            throw 'FFmpeg output pipes did not close within five seconds.'
        }
        $stdout = $outTask.GetAwaiter().GetResult()
        $stderr = $errTask.GetAwaiter().GetResult()
        $stdout | Set-Content -LiteralPath ($LogPrefix + '.stdout.log') -Encoding UTF8
        $stderr | Set-Content -LiteralPath ($LogPrefix + '.stderr.log') -Encoding UTF8
        $result.Output = $stdout + "`n" + $stderr
    } catch {
        $result.Error = $_.Exception.Message
        $result.Error | Set-Content -LiteralPath ($LogPrefix + '.error.log') -Encoding UTF8
    } finally {
        if ($started -and -not $process.HasExited) {
            try { $process.Kill(); $process.WaitForExit(5000) | Out-Null } catch { }
        }
        $process.Dispose()
        $watch.Stop()
    }
    $result.ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
    return [pscustomobject]$result
}

function Get-ImageAnalysis([string]$Output) {
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $black = @([regex]::Matches($Output, 'lavfi\.blackframe\.pblack=([0-9.]+)') |
        ForEach-Object { [double]::Parse($_.Groups[1].Value, $culture) })
    $luma = @([regex]::Matches($Output, 'lavfi\.signalstats\.YAVG=([0-9.]+)') |
        ForEach-Object { [double]::Parse($_.Groups[1].Value, $culture) })
    $nearBlack = $null
    if ($black.Count -gt 0 -and $black.Count -eq $luma.Count) {
        # blackframe reports integer percentages. A black screen with the cursor
        # may report 99, so also check mean luma. This remains a diagnostic hint.
        $nearBlack = @($black | Where-Object { $_ -lt 99 }).Count -eq 0 -and
            @($luma | Where-Object { $_ -gt 24 }).Count -eq 0
    }
    return [pscustomobject]@{
        SampleCount = $luma.Count
        BlackPixelPercent = $black
        AverageLuma = $luma
        AllSamplesNearlyBlack = $nearBlack
    }
}

if (-not $FfmpegPath) {
    $searchRoots = @((Get-Location).Path, $PSScriptRoot, (Split-Path -Parent $PSScriptRoot))
    $candidates = @($searchRoots | ForEach-Object { Join-Path $_ 'tools\ffmpeg\ffmpeg.exe' })
    $candidates += Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\third-party\ffmpeg\ffmpeg.exe'
    $FfmpegPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $FfmpegPath -or -not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) {
    throw 'Bundled FFmpeg was not found. Specify -FfmpegPath with the path to tools\ffmpeg\ffmpeg.exe.'
}
$FfmpegPath = (Resolve-Path -LiteralPath $FfmpegPath).Path
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$runDirectory = Join-Path $outputRoot ('desktop-capture-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

# Query only this process's session. Do not enumerate users or alter RDP settings.
if (-not ('DesktopCaptureDiagnostic.Session' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace DesktopCaptureDiagnostic {
    public static class Session {
        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);
        public static int Query(int sessionId, int infoClass, bool shortValue) {
            IntPtr buffer; int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buffer, out bytes)) return -1;
            try { return bytes < (shortValue ? 2 : 4) ? -1 : (shortValue ? Marshal.ReadInt16(buffer) : Marshal.ReadInt32(buffer)); }
            finally { WTSFreeMemory(buffer); }
        }
    }
}
'@
}
Add-Type -AssemblyName System.Windows.Forms
function Get-CurrentSessionDiagnostic {
    $currentSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $stateCode = [DesktopCaptureDiagnostic.Session]::Query($currentSessionId, 8, $false)
    $protocolCode = [DesktopCaptureDiagnostic.Session]::Query($currentSessionId, 16, $true)
    $states = @('Active', 'Connected', 'ConnectQuery', 'Shadow', 'Disconnected', 'Idle', 'Listen', 'Reset', 'Down', 'Init')
    $state = if ($stateCode -ge 0 -and $stateCode -lt $states.Count) { $states[$stateCode] } else { 'Unknown' }
    return [pscustomobject]@{
        CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
        SessionId = $currentSessionId
        State = $state
        ProtocolCode = $protocolCode # 0 = console, 2 = RDP, -1 = unavailable
        RemoteSessionMetric = [DesktopCaptureDiagnostic.Session]::GetSystemMetrics(0x1000) -ne 0
        UserInteractive = [Environment]::UserInteractive
    }
}

$screens = @([Windows.Forms.Screen]::AllScreens | ForEach-Object {
    [pscustomobject]@{ Primary = $_.Primary; X = $_.Bounds.X; Y = $_.Bounds.Y; Width = $_.Bounds.Width; Height = $_.Bounds.Height }
})
$primary = [Windows.Forms.Screen]::PrimaryScreen.Bounds
$summary = [ordered]@{
    StartedAtUtc = [DateTime]::UtcNow.ToString('o')
    FfmpegSha256 = (Get-FileHash -LiteralPath $FfmpegPath -Algorithm SHA256).Hash
    SessionBefore = Get-CurrentSessionDiagnostic
    Screens = $screens
    GdiRegion = @{ X = $primary.X; Y = $primary.Y; Width = $primary.Width; Height = $primary.Height }
    DxgiOutputIndex = $DxgiOutputIndex
    DurationSeconds = $DurationSeconds
    Candidates = @()
}
Write-Host "Diagnostic output: $runDirectory"
Write-Host 'Keep this test machine unlocked and the RDP client open and visible. Put ENCY on the recorded display.'
Write-Host 'GDI records the primary display; DXGI records the selected output index (these may differ on multiple displays).'
Write-Host 'Switch to ENCY now. Capture starts in three seconds.'
Start-Sleep -Seconds 3

$version = Invoke-BoundedFFmpeg -Executable $FfmpegPath -ArgumentList @('-hide_banner', '-version') -LogPrefix (Join-Path $runDirectory 'ffmpeg-version') -TimeoutSeconds 10
$summary.FfmpegVersionExitCode = $version.ExitCode
foreach ($backend in @('gdi', 'dxgi')) {
    $prefix = Join-Path $runDirectory $backend
    $video = $prefix + '.mkv'
    $inputArguments = if ($backend -eq 'gdi') {
        @('-f', 'gdigrab', '-framerate', '5', '-draw_mouse', '1', '-offset_x', [string]$primary.X,
            '-offset_y', [string]$primary.Y, '-video_size', "$($primary.Width)x$($primary.Height)", '-i', 'desktop')
    } else {
        @('-f', 'lavfi', '-i', "ddagrab=output_idx=${DxgiOutputIndex}:framerate=5:draw_mouse=1",
            '-vf', 'hwdownload,format=bgra')
    }
    $captureArguments = @('-hide_banner', '-nostdin', '-y') + $inputArguments +
        @('-t', [string]$DurationSeconds, '-an', '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '23', '-pix_fmt', 'yuv420p', $video)
    Write-Host "Recording $backend for $DurationSeconds seconds..."
    $capture = Invoke-BoundedFFmpeg -Executable $FfmpegPath -ArgumentList $captureArguments -LogPrefix ($prefix + '-capture') -TimeoutSeconds ($DurationSeconds + 15)
    $candidate = [ordered]@{
        Backend = $backend; CaptureExitCode = $capture.ExitCode; CaptureTimedOut = $capture.TimedOut
        CaptureError = $capture.Error; CaptureElapsedSeconds = $capture.ElapsedSeconds
        SessionAfter = Get-CurrentSessionDiagnostic; Status = 'CaptureFailed'; Analysis = $null; Frames = @()
    }
    if (-not $capture.TimedOut -and $null -eq $capture.Error -and $capture.ExitCode -eq 0 -and (Test-Path -LiteralPath $video)) {
        $filter = 'fps=1,blackframe=amount=0:threshold=32,metadata=mode=print:key=lavfi.blackframe.pblack,signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG'
        $analysisRun = Invoke-BoundedFFmpeg -Executable $FfmpegPath -ArgumentList @('-hide_banner', '-nostdin', '-i', $video, '-vf', $filter, '-an', '-f', 'null', '-') -LogPrefix ($prefix + '-analysis') -TimeoutSeconds 15
        $candidate.Analysis = Get-ImageAnalysis $analysisRun.Output
        $candidate.AnalysisExitCode = $analysisRun.ExitCode
        $candidate.AnalysisTimedOut = $analysisRun.TimedOut
        $candidate.AnalysisError = $analysisRun.Error
        $candidate.Status = 'AnalysisFailed'
        if ($analysisRun.ExitCode -eq 0 -and -not $analysisRun.TimedOut -and $null -eq $analysisRun.Error -and $null -ne $candidate.Analysis.AllSamplesNearlyBlack) {
            $candidate.Status = if ($candidate.Analysis.AllSamplesNearlyBlack) { 'NearlyBlack' } else { 'ImagePresentRequiresReview' }
        }
        foreach ($second in @(0, ($DurationSeconds - 1))) {
            $frame = $prefix + '-frame-' + $second + '.png'
            $frameRun = Invoke-BoundedFFmpeg -Executable $FfmpegPath -ArgumentList @('-hide_banner', '-nostdin', '-y', '-ss', [string]$second, '-i', $video, '-frames:v', '1', '-update', '1', $frame) -LogPrefix ($prefix + '-frame-' + $second) -TimeoutSeconds 15
            $candidate.Frames += [pscustomobject]@{ Second = $second; Written = (Test-Path -LiteralPath $frame); ExitCode = $frameRun.ExitCode; TimedOut = $frameRun.TimedOut; Error = $frameRun.Error }
        }
    }
    $summary.Candidates += [pscustomobject]$candidate
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding UTF8
    Write-Host "$backend result: $($candidate.Status)"
}
$summary.CompletedAtUtc = [DateTime]::UtcNow.ToString('o')
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding UTF8
Write-Host 'Review both PNG pairs and MKV files. An active session or nonblack pixels alone do not prove ENCY is visible.'
Write-Host 'This diagnostic does not change RDP/session/registry settings or enable recording in a disconnected session.'
if (@($summary.Candidates | Where-Object { $_.Status -eq 'ImagePresentRequiresReview' }).Count -eq 0) { exit 1 }
