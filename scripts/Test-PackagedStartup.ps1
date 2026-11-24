[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$TesterExecutable)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$checkDirectory = Join-Path $repositoryRoot ('artifacts\startup-smoke\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkDirectory -Force | Out-Null
$isolatedTester = Join-Path $checkDirectory 'ProjectTestingUtility.exe'
Copy-Item -LiteralPath $TesterExecutable -Destination $isolatedTester

# Only this synthetic configuration is read. A local closed endpoint protects
# against accidental external calls if credential validation ever regresses.
@{ BaseUrl = 'http://127.0.0.1:1'; Login = ''; Password = ''; CamlmAuthKey = '' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $checkDirectory 'settings.json') -Encoding UTF8

function Test-FailedStartup([string]$arguments, [string]$expectedStage) {
    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $isolatedTester
    $process.StartInfo.Arguments = $arguments
    $process.StartInfo.WorkingDirectory = $checkDirectory
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    try {
        $process.Start() | Out-Null
        if (-not $process.WaitForExit(15000)) {
            $process.Kill()
            throw 'Packaged startup check timed out.'
        }
        if ($process.ExitCode -ne 1) { throw "Expected startup exit code 1, got $($process.ExitCode)." }
        $status = Get-Content -LiteralPath (Join-Path $checkDirectory 'run-status.json') -Raw | ConvertFrom-Json
        if ($status.State -ne 'Failed' -or $status.Stage -ne $expectedStage) {
            throw "Unexpected startup state $($status.State)/$($status.Stage); expected Failed/$expectedStage."
        }
        Write-Host "PASS packaged tester startup failure: $expectedStage, exit 1, explicit status."
    } finally {
        $process.Dispose()
    }
}

Test-FailedStartup '' 'ValidateLaunch'

# This file is intentionally not an executable. Credentials must be validated
# before launching CAM or touching any CAM process; no real CAM path is used.
$fakeCam = Join-Path $checkDirectory ('StartupSmokeFakeCam-' + [Guid]::NewGuid().ToString('N') + '.exe')
[IO.File]::WriteAllBytes($fakeCam, [byte[]]@())
Test-FailedStartup ('"' + $fakeCam + '"') 'ProjectService'
$status = Get-Content -LiteralPath (Join-Path $checkDirectory 'run-status.json') -Raw | ConvertFrom-Json
if ($status.Message -notmatch 'authorization failed') { throw 'Blank credentials were not reported as an authorization failure.' }
if (Test-Path -LiteralPath (Join-Path $checkDirectory 'diagnostics')) { throw 'A project attempt started before authentication succeeded.' }
Write-Host "Packaged startup smoke artifacts: $checkDirectory"
