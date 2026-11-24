[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cacheDirectory = Join-Path $repositoryRoot 'artifacts\ffmpeg-cache'
$bundleDirectory = Join-Path $repositoryRoot 'artifacts\third-party\ffmpeg'
$version = '8.1.2'
$archiveName = "ffmpeg-$version-essentials_build.zip"
$archivePath = Join-Path $cacheDirectory $archiveName
$archiveSha256 = 'db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec'
$archiveUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$version/$archiveName"
$sourceCommit = '38b88335f9'

function Get-FFmpegOutput([string]$executable, [string]$arguments) {
    # FFmpeg writes informational output to stderr; PowerShell 5.1 must not
    # mistake it for a terminating NativeCommandError under ErrorAction Stop.
    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $executable
    $process.StartInfo.Arguments = $arguments
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    try {
        $process.Start() | Out-Null
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $standardOutput.GetAwaiter().GetResult() + $standardError.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "FFmpeg exited with $($process.ExitCode): $output" }
        return $output
    } finally {
        $process.Dispose()
    }
}

New-Item -ItemType Directory -Path $cacheDirectory, $bundleDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $archivePath)) {
    Write-Host "Downloading pinned FFmpeg $version (109 MB)..."
    $partialPath = "$archivePath.download"
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe --fail --location --retry 3 --connect-timeout 30 --max-time 1800 --output $partialPath $archiveUrl
        if ($LASTEXITCODE -ne 0) { throw "FFmpeg download failed (curl exit $LASTEXITCODE)." }
    } else {
        # Windows PowerShell 5.1 may otherwise negotiate obsolete TLS versions.
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $archiveUrl -OutFile $partialPath -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash -ne $archiveSha256) {
        throw "FFmpeg checksum mismatch. Do not use $partialPath."
    }
    Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
}

if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $archiveSha256) {
    throw "Cached FFmpeg checksum mismatch: $archivePath. Remove that file and run the script again."
}

# Extract only the executable and upstream notices; ffplay/ffprobe are not needed.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $prefix = "ffmpeg-$version-essentials_build/"
    foreach ($relativePath in @('bin/ffmpeg.exe', 'LICENSE', 'README.txt')) {
        $entry = $archive.GetEntry($prefix + $relativePath)
        if ($null -eq $entry) { throw "Missing expected archive member: $relativePath" }
        $fileName = [IO.Path]::GetFileName($relativePath)
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $bundleDirectory $fileName), $true)
    }
} finally {
    $archive.Dispose()
}

$ffmpegPath = Join-Path $bundleDirectory 'ffmpeg.exe'
$buildDetails = Get-FFmpegOutput $ffmpegPath '-hide_banner -version'
$buildDetails | Set-Content -LiteralPath (Join-Path $bundleDirectory 'BUILD-CONFIGURATION.txt') -Encoding UTF8
if ($buildDetails -notmatch '--enable-libx264') { throw 'The recorder requires the libx264 encoder.' }
$devices = Get-FFmpegOutput $ffmpegPath '-hide_banner -devices'
if ($devices -notmatch '\bgdigrab\b') { throw 'The recorder requires the Windows gdigrab input.' }

$noticeTemplate = Join-Path $PSScriptRoot 'ffmpeg-NOTICE.txt'
Copy-Item -LiteralPath $noticeTemplate -Destination (Join-Path $bundleDirectory 'NOTICE.txt') -Force
@{
    version = $version
    variant = 'Gyan essentials static win64 GPLv3'
    downloadUrl = $archiveUrl
    archiveSha256 = $archiveSha256
    executableSha256 = (Get-FileHash -LiteralPath $ffmpegPath -Algorithm SHA256).Hash.ToLowerInvariant()
    upstreamChecksumUrl = "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName.sha256"
    ffmpegSourceCommit = $sourceCommit
    ffmpegSourceUrl = "https://github.com/FFmpeg/FFmpeg/tree/$sourceCommit"
    ffmpegSourceArchiveUrl = "https://github.com/FFmpeg/FFmpeg/archive/$sourceCommit.tar.gz"
    buildPublisher = 'https://www.gyan.dev/ffmpeg/builds/'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundleDirectory 'manifest.json') -Encoding UTF8

Write-Host "FFmpeg $version verified and prepared: $bundleDirectory"
