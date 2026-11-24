[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot

# Resource presets become part of the executable. Never publish credentials.
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'ProjectTestingMonitor\Resources') -Filter 'settings*.json' -File |
    ForEach-Object {
        $preset = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        foreach ($credentialName in @('Login', 'Password', 'CamlmAuthKey')) {
            $credential = $preset.PSObject.Properties[$credentialName]
            if ($null -ne $credential -and -not [string]::IsNullOrWhiteSpace([string]$credential.Value)) {
                throw "Remove $credentialName from preset $($_.Name) before publishing."
            }
        }
    }

& (Join-Path $PSScriptRoot 'Prepare-FFmpeg.ps1')

# A unique output avoids mixing this build with an older tester or stale files.
$releaseName = 'ProjectTester-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$releaseRoot = Join-Path $repositoryRoot "artifacts\releases\$releaseName"
$testerOutput = Join-Path $releaseRoot 'tester'
$monitorOutput = Join-Path $releaseRoot 'ProjectTester'
$ffmpegDirectory = Join-Path $repositoryRoot 'artifacts\third-party\ffmpeg'
New-Item -ItemType Directory -Path $testerOutput, $monitorOutput -Force | Out-Null

& dotnet publish (Join-Path $repositoryRoot 'ProjectTestingUtility\ProjectTestingUtility.csproj') `
    -c Release -r win-x64 --self-contained true -o $testerOutput
if ($LASTEXITCODE -ne 0) { throw 'Publishing ProjectTestingUtility failed.' }

$testerExecutable = Join-Path $testerOutput 'ProjectTestingUtility.exe'
if (-not (Test-Path -LiteralPath $testerExecutable)) { throw 'Published tester executable is missing.' }
& (Join-Path $PSScriptRoot 'Test-PackagedStartup.ps1') -TesterExecutable $testerExecutable

& dotnet publish (Join-Path $repositoryRoot 'ProjectTestingMonitor\ProjectTesterMonitor.csproj') `
    -c Release -r win-x64 --self-contained true -o $monitorOutput `
    "-p:TesterExecutablePath=$testerExecutable" "-p:FfmpegBundleDirectory=$ffmpegDirectory" '-p:RequireDiagnosticBundle=true'
if ($LASTEXITCODE -ne 0) { throw 'Publishing ProjectTesterMonitor failed.' }

# The monitor is a single-file host; inspect the managed build artifact that
# was bundled into it to ensure the embedded tester is exactly this build.
$monitorAssemblyPath = Join-Path $repositoryRoot 'ProjectTestingMonitor\bin\Release\net10.0-windows\win-x64\ProjectTesterMonitor.dll'
$monitorAssembly = [Reflection.Assembly]::LoadFile($monitorAssemblyPath)
$testerResource = $monitorAssembly.GetManifestResourceStream('ProjectTesterMonitor.Resources.ProjectTestingUtility.exe')
if ($null -eq $testerResource) { throw 'The monitor does not contain its tester resource.' }
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $embeddedTesterHash = [BitConverter]::ToString($sha256.ComputeHash($testerResource)).Replace('-', '')
    if ($embeddedTesterHash -ne (Get-FileHash -LiteralPath $testerExecutable -Algorithm SHA256).Hash) {
        throw 'The monitor embedded a stale tester executable.'
    }
} finally {
    $testerResource.Dispose()
    $sha256.Dispose()
}

foreach ($required in @('ProjectTesterMonitor.exe', 'tools\ffmpeg\ffmpeg.exe', 'tools\ffmpeg\LICENSE', 'tools\ffmpeg\manifest.json', 'tools\ffmpeg\NOTICE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $monitorOutput $required))) {
        throw "Published package is missing $required."
    }
}
if ((Get-FileHash -LiteralPath (Join-Path $monitorOutput 'tools\ffmpeg\ffmpeg.exe')).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $ffmpegDirectory 'ffmpeg.exe')).Hash) {
    throw 'Packaged FFmpeg does not match the verified build.'
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\PORTABLE-RELEASE.md') -Destination (Join-Path $monitorOutput 'README.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\RDP-RECORDING.md') -Destination (Join-Path $monitorOutput 'RDP-RECORDING.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts\Test-DesktopRecording.ps1') -Destination (Join-Path $monitorOutput 'Test-DesktopRecording.ps1')
@{
    createdUtc = [DateTime]::UtcNow.ToString('o')
    testerSha256 = $embeddedTesterHash.ToLowerInvariant()
    monitorSha256 = (Get-FileHash -LiteralPath (Join-Path $monitorOutput 'ProjectTesterMonitor.exe')).Hash.ToLowerInvariant()
    ffmpegSha256 = (Get-FileHash -LiteralPath (Join-Path $monitorOutput 'tools\ffmpeg\ffmpeg.exe')).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $monitorOutput 'build-manifest.json') -Encoding UTF8
$archivePath = Join-Path $releaseRoot "$releaseName-win-x64.zip"
Compress-Archive -LiteralPath $monitorOutput -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding ASCII
Write-Host "Portable package: $archivePath"
Write-Host "SHA-256: $archiveHash"
