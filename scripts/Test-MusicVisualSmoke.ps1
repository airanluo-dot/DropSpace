# Synthetic native WinUI control capture only. No user data, account, publication, or display-setting changes.
[CmdletBinding()]
param(
    [string]$ExecutablePath = 'artifacts/release/DropSpace.exe',
    [ValidateSet('en-US', 'zh-CN')][string]$Language = 'en-US',
    [string]$OutputDirectory = 'artifacts/music-visual',
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native music visual diagnostics require Windows.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = if ([IO.Path]::IsPathFullyQualified($ExecutablePath)) { $ExecutablePath } else { Join-Path $repo $ExecutablePath }
$output = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) { $OutputDirectory } else { Join-Path $repo $OutputDirectory }
$output = Join-Path $output $Language
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The visual diagnostic executable does not exist.' }
if (Test-Path -LiteralPath $output) { throw 'The visual evidence destination must be fresh.' }
if (Get-Process -Name DropSpace -ErrorAction SilentlyContinue) { throw 'Close other DropSpace instances before isolated visual diagnostics.' }
New-Item -ItemType Directory -Path $output | Out-Null
$root = Join-Path ([IO.Path]::GetTempPath()) ('DropSpace-visual-' + [guid]::NewGuid().ToString('N'))
$previousRoot = $env:DROPSPACE_TEST_DATA_ROOT
$child = $null
$status = 'failed'
$reason = 'Process did not complete.'
try {
    $env:DROPSPACE_TEST_DATA_ROOT = $root
    $child = Start-Process -FilePath $exe -ArgumentList '--test-mode', '--music-visual-smoke', '--smoke-language', $Language -PassThru
    if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
        $reason = 'Native UI diagnostic timed out; graphical session or rendering may be unavailable.'
        $child.Kill($true)
        $child.WaitForExit()
        throw $reason
    }
    $captures = Join-Path $root 'captures'
    if (Test-Path -LiteralPath $captures) { Get-ChildItem -LiteralPath $captures -File | Copy-Item -Destination $output }
    $manifestPath = Join-Path $output 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        $reason = "Native UI capture produced no manifest (exit $($child.ExitCode)); no visual pass is claimed."
        throw $reason
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $status = $manifest.status
    $reason = $manifest.failure
    if ($child.ExitCode -ne 0 -or $status -ne 'passed') { throw "Native visual diagnostic $status (exit $($child.ExitCode)): $reason" }
    if ($manifest.language -cne $Language -or $manifest.evidenceKind -cne 'native-winui-control-render-target-bitmap') { throw 'Visual evidence identity mismatch.' }
    if (@($manifest.captures).Count -ne 4) { throw 'Expected two real controls in light and dark themes.' }
    foreach ($capture in $manifest.captures) {
        if ([IO.Path]::GetFileName($capture.File) -cne $capture.File -or $capture.File -notlike '*.png') { throw 'Invalid capture filename.' }
        $image = Join-Path $output $capture.File
        if (-not (Test-Path -LiteralPath $image -PathType Leaf) -or (Get-Item -LiteralPath $image).Length -le 100) { throw 'Native capture is missing or empty.' }
        if ($capture.PixelWidth -le 0 -or $capture.PixelHeight -le 0 -or @($capture.Failures).Count -ne 0) { throw 'Native capture has unresolved geometry failures.' }
    }
    $status = 'passed'
    $reason = 'Native component PNGs and measured geometry produced; human visual review still required.'
    Write-Host $reason
}
catch {
    if ($status -ne 'blocked') { $status = 'failed' }
    if (-not $reason -or $reason -eq 'Process did not complete.') { $reason = $_.Exception.Message }
    throw
}
finally {
    if ($null -ne $child -and -not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
    $captures = Join-Path $root 'captures'
    if (Test-Path -LiteralPath $captures) { Get-ChildItem -LiteralPath $captures -File | Copy-Item -Destination $output -Force }
    [ordered]@{
        status = $status; reason = $reason; language = $Language
        executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        commit = $env:GITHUB_SHA; exitCode = $(if ($null -ne $child -and $child.HasExited) { $child.ExitCode } else { $null })
        noPublication = $true; dataSource = 'synthetic-only'; changesGlobalDisplaySettings = $false
    } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'launcher.json') -Encoding utf8
    $env:DROPSPACE_TEST_DATA_ROOT = $previousRoot
    # The GUID directory was allocated by this launcher and never targets a user's data directory.
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
