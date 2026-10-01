param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('DropSpace-runtime-notices-' + [Guid]::NewGuid().ToString('N'))
try {
    $source = Join-Path $fixture 'source'
    $output = Join-Path $fixture 'notices.txt'
    foreach ($file in @('LICENSE', 'vendor/cpp-httplib/LICENSE', 'licenses/LICENSE-jsonhpp', 'vendor/extra/NOTICE')) {
        $path = Join-Path $source $file
        New-Item ([IO.Path]::GetDirectoryName($path)) -ItemType Directory -Force | Out-Null
        Set-Content $path ('fixture notice: ' + $file) -Encoding utf8
    }
    # No build tree or generated license.cpp exists: completion-only must work.
    & (Join-Path $PSScriptRoot 'Collect-AiRuntimeNotices.ps1') -Source $source -OutputPath $output
    $actual = Get-Content $output -Raw
    foreach ($file in @('LICENSE', 'vendor/cpp-httplib/LICENSE', 'licenses/LICENSE-jsonhpp', 'vendor/extra/NOTICE')) {
        if (-not $actual.Contains('fixture notice: ' + $file)) { throw "Notice omitted: $file" }
    }
    Remove-Item (Join-Path $source 'licenses/LICENSE-jsonhpp')
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Collect-AiRuntimeNotices.ps1') -Source $source -OutputPath $output }
    catch { if ($_.Exception.Message -like '*Missing required runtime notice:*') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw 'A missing required source notice must fail closed.' }
    Write-Host 'Runtime source-notice regressions passed: completion-only fixture, all notices present, required-file rejection.'
}
finally { if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force } }
