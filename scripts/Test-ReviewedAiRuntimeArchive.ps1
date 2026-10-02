[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('DropSpace-runtime-archive-' + [Guid]::NewGuid().ToString('N'))
New-Item $work -ItemType Directory | Out-Null
$extract = Join-Path $PSScriptRoot 'Expand-ReviewedAiRuntime.ps1'
$passed = 0
$payload = [ordered]@{ 'native/engine.dll' = 'synthetic engine'; 'LICENSE' = 'synthetic license'; 'runtime-manifest.json' = '{}' }
function New-ArchiveFixture([string]$name, [string]$fault = '') {
    $archivePath = Join-Path $work "$name.zip"
    $zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $payload.GetEnumerator()) {
            if ($fault -eq 'missing' -and $item.Key -eq 'LICENSE') { continue }
            $entry = $zip.CreateEntry($item.Key)
            $value = if ($fault -eq 'changed' -and $item.Key -eq 'LICENSE') { 'changed!! license' } else { $item.Value }
            $stream = $entry.Open()
            try { $bytes = [Text.Encoding]::UTF8.GetBytes($value); $stream.Write($bytes) } finally { $stream.Dispose() }
        }
        $extra = switch ($fault) {
            'traversal' { '../outside.dll' }
            'backslash' { 'native\engine.dll' }
            'duplicate' { 'native/engine.dll' }
            'duplicate-case' { 'NATIVE/ENGINE.DLL' }
            'extra' { 'extra.dll' }
            'directory' { 'unlisted/' }
            'link' { 'link.dll' }
            default { '' }
        }
        if ($extra) {
            $entry = $zip.CreateEntry($extra)
            if ($fault -eq 'link') { $entry.ExternalAttributes = 0xA000 -shl 16 }
        }
    } finally { $zip.Dispose() }
    $files = foreach ($item in $payload.GetEnumerator()) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($item.Value)
        @{ path = $item.Key; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant(); bytes = $bytes.Length }
    }
    $hash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($fault -eq 'hash') { $hash = '0' * 64 }
    $contract = @{
        schemaVersion = 1; repository = 'airanluo-dot/DropSpace'; workflowPath = '.github/workflows/release.yml'
        runId = 123; runAttempt = 2; artifactId = 456; artifactName = 'ai-candidate-runtime-123-2'
        headCommit = 'a' * 40; checkoutCommit = 'b' * 40; archiveSha256 = $hash; files = @($files)
    }
    $contractPath = Join-Path $work "$name.json"
    $contract | ConvertTo-Json -Depth 6 | Set-Content $contractPath -Encoding utf8
    return @{ Archive = $archivePath; Contract = $contractPath; Output = (Join-Path $work "$name-output") }
}
try {
    $good = New-ArchiveFixture 'good'
    & $extract -ArchivePath $good.Archive -ContractPath $good.Contract -OutputDirectory $good.Output
    if ([IO.File]::ReadAllText((Join-Path $good.Output 'native/engine.dll')) -cne 'synthetic engine') { throw 'Valid archive was not preserved.' }
    $passed++
    $rejected = $false
    try { & $extract -ArchivePath $good.Archive -ContractPath $good.Contract -OutputDirectory $good.Output } catch { $rejected = $true }
    if (-not $rejected) { throw 'Existing output directory was not rejected.' }
    $passed++
    foreach ($fault in @('hash', 'missing', 'changed', 'traversal', 'backslash', 'duplicate', 'duplicate-case', 'extra', 'directory', 'link')) {
        $fixture = New-ArchiveFixture $fault $fault
        $rejected = $false
        try { & $extract -ArchivePath $fixture.Archive -ContractPath $fixture.Contract -OutputDirectory $fixture.Output } catch { $rejected = $true }
        if (-not $rejected) { throw "Unsafe archive fixture was accepted: $fault" }
        $passed++
    }
    if (Test-Path (Join-Path $work 'outside.dll')) { throw 'Archive traversal escaped its output directory.' }
    Write-Host "Reviewed runtime archive regression checks passed: $passed"
    $global:LASTEXITCODE = 0
} finally { Remove-Item $work -Recurse -Force }
