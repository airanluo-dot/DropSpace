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
    $sdk = Join-Path $fixture 'sdk'
    foreach ($component in @('Vulkan-Headers', 'Vulkan-Hpp', 'SPIRV-Headers')) {
        $notice = Join-Path $sdk "Notices/$component/LICENSE.txt"
        New-Item ([IO.Path]::GetDirectoryName($notice)) -ItemType Directory -Force | Out-Null
        Set-Content -LiteralPath $notice -Value "Synthetic notice fixture for $component; not a shipping license." -Encoding UTF8
    }
    & (Join-Path $PSScriptRoot 'Collect-AiRuntimeNotices.ps1') -Source $source -OutputPath $output -VulkanSdk $sdk
    $actual = Get-Content -LiteralPath $output -Raw
    foreach ($component in @('Vulkan-Headers', 'Vulkan-Hpp', 'SPIRV-Headers')) {
        if (-not $actual.Contains("Synthetic notice fixture for $component")) { throw "Vulkan notice omitted: $component" }
    }
    Remove-Item -LiteralPath (Join-Path $sdk 'Notices/Vulkan-Hpp/LICENSE.txt')
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Collect-AiRuntimeNotices.ps1') -Source $source -OutputPath $output -VulkanSdk $sdk }
    catch { if ($_.Exception.Message -like '*Missing Vulkan-Hpp license attribution*') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw 'A missing Vulkan-Hpp license attribution must fail closed.' }
    Remove-Item (Join-Path $source 'licenses/LICENSE-jsonhpp')
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Collect-AiRuntimeNotices.ps1') -Source $source -OutputPath $output }
    catch { if ($_.Exception.Message -like '*Missing required runtime notice:*') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw 'A missing required source notice must fail closed.' }
    Write-Host 'Runtime source-notice regressions passed: completion-only, Vulkan notice collection, required-file and missing-component rejection.'
}
finally {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $absoluteFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $absoluteFixture.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime notice fixture escaped the temporary directory.'
    }
    if (Test-Path -LiteralPath $absoluteFixture) { Remove-Item -LiteralPath $absoluteFixture -Recurse -Force }
}
