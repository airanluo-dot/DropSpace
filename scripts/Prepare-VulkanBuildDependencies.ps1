param(
    [string]$WorkspaceRoot = (Join-Path $PSScriptRoot '../artifacts/vulkan-dependencies'),
    [string]$VcpkgExecutable = '',
    [ValidateRange(1, 8)][int]$BuildConcurrency = 2,
    [switch]$GitHubActions
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The shipping Vulkan build dependencies require Windows x64.' }
$workspace = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$manifestRoot = Join-Path $PSScriptRoot 'build-dependencies/vulkan'
$manifest = Get-Content -LiteralPath (Join-Path $manifestRoot 'vcpkg.json') -Raw | ConvertFrom-Json
$baseline = $manifest.'builtin-baseline'
if ($baseline -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid pinned vcpkg baseline.' }

function New-OwnedDirectory([string]$Relative) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $workspace $Relative))
    if (-not $absolute.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Vulkan dependency destination escaped its workspace.'
    }
    for ($ancestor = $absolute; $ancestor -and $ancestor.Length -ge $workspace.Length; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Vulkan dependency workspace must not traverse a reparse point: $ancestor"
            }
        }
    }
    New-Item -Path $absolute -ItemType Directory -Force | Out-Null
    return $absolute
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

$vcpkgRoot = New-OwnedDirectory 'tools/vcpkg'
if (-not (Test-Path -LiteralPath (Join-Path $vcpkgRoot '.git'))) {
    if (@(Get-ChildItem -LiteralPath $vcpkgRoot -Force).Count -ne 0) { throw 'Preserving an unexpected nonempty vcpkg directory.' }
    Invoke-Checked 'git' @('init', $vcpkgRoot)
    Invoke-Checked 'git' @('-C', $vcpkgRoot, 'remote', 'add', 'origin', 'https://github.com/microsoft/vcpkg.git')
    Invoke-Checked 'git' @('-C', $vcpkgRoot, '-c', 'fetch.fsckObjects=true', 'fetch', '--depth=1', 'origin', $baseline)
    Invoke-Checked 'git' @('-C', $vcpkgRoot, 'checkout', '--detach', 'FETCH_HEAD')
}
$actual = (& git -C $vcpkgRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -cne $baseline) { throw 'The isolated vcpkg source does not match the pinned baseline.' }
if (@(& git -C $vcpkgRoot status --porcelain --untracked-files=no).Count -ne 0) { throw 'The pinned vcpkg source has tracked modifications.' }

# All caches are process-scoped. Never run vcpkg integrate or install a driver/SDK globally.
$env:VCPKG_ROOT = $vcpkgRoot
$env:VCPKG_DISABLE_METRICS = '1'
$env:VCPKG_BINARY_SOURCES = 'clear'
$env:VCPKG_MAX_CONCURRENCY = [string]$BuildConcurrency
$env:VCPKG_DOWNLOADS = New-OwnedDirectory 'temp/vcpkg-downloads'
$env:X_VCPKG_REGISTRIES_CACHE = New-OwnedDirectory 'temp/vcpkg-registries'
$env:VCPKG_DEFAULT_BINARY_CACHE = New-OwnedDirectory 'temp/vcpkg-binary-cache'
$installed = New-OwnedDirectory 'tools/vulkan-installed'
$buildtrees = New-OwnedDirectory 'build/vcpkg'
$packages = New-OwnedDirectory 'temp/vcpkg-packages'

if (-not $VcpkgExecutable) {
    $VcpkgExecutable = Join-Path $vcpkgRoot 'vcpkg.exe'
    if (-not (Test-Path -LiteralPath $VcpkgExecutable -PathType Leaf)) {
        Invoke-Checked (Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat') @('-disableMetrics')
    }
}
if (-not (Test-Path -LiteralPath $VcpkgExecutable -PathType Leaf)) { throw 'The selected vcpkg executable is missing.' }
Invoke-Checked $VcpkgExecutable @('install', '--disable-metrics', '--triplet=x64-windows',
    "--vcpkg-root=$vcpkgRoot", "--x-manifest-root=$manifestRoot", "--x-install-root=$installed",
    "--x-buildtrees-root=$buildtrees", "--x-packages-root=$packages")

$tripletRoot = Join-Path $installed 'x64-windows'
foreach ($required in @('tools/shaderc/glslc.exe', 'include/vulkan/vulkan.h', 'include/vulkan/vulkan.hpp',
    'include/spirv/unified1/spirv.hpp', 'lib/vulkan-1.lib', 'share/vulkan-headers/copyright', 'share/spirv-headers/copyright')) {
    if (-not (Test-Path -LiteralPath (Join-Path $tripletRoot $required) -PathType Leaf)) { throw "Missing Vulkan build dependency: $required" }
}
$sdk = New-OwnedDirectory ('tools/vulkan-sdk-' + $baseline.Substring(0, 12))
$bin = New-OwnedDirectory ('tools/vulkan-sdk-' + $baseline.Substring(0, 12) + '/Bin')
$include = New-OwnedDirectory ('tools/vulkan-sdk-' + $baseline.Substring(0, 12) + '/Include')
$lib = New-OwnedDirectory ('tools/vulkan-sdk-' + $baseline.Substring(0, 12) + '/Lib')
$share = New-OwnedDirectory ('tools/vulkan-sdk-' + $baseline.Substring(0, 12) + '/share')
Copy-Item -Path (Join-Path $tripletRoot 'tools/shaderc/*') -Destination $bin -Recurse -Force
Copy-Item -Path (Join-Path $tripletRoot 'include/*') -Destination $include -Recurse -Force
Copy-Item -LiteralPath (Join-Path $tripletRoot 'lib/vulkan-1.lib') -Destination (Join-Path $lib 'vulkan-1.lib') -Force
Copy-Item -LiteralPath (Join-Path $tripletRoot 'share/spirv-headers') -Destination $share -Recurse -Force
foreach ($component in @('shaderc', 'spirv-headers', 'spirv-tools', 'glslang', 'vulkan-headers', 'vulkan-loader')) {
    $noticeDirectory = New-OwnedDirectory ('tools/vulkan-sdk-' + $baseline.Substring(0, 12) + '/Notices/' + $component)
    Copy-Item -LiteralPath (Join-Path $tripletRoot "share/$component/copyright") -Destination (Join-Path $noticeDirectory 'LICENSE.txt') -Force
}
# Vulkan-Headers distributes the generated Vulkan-Hpp headers with the same
# Khronos SPDX license. Retain the complete upstream texts, not just web links.
$installedHppHash = (Get-FileHash -LiteralPath (Join-Path $tripletRoot 'include/vulkan/vulkan.hpp') -Algorithm SHA256).Hash
$headerSources = @(Get-ChildItem -LiteralPath (Join-Path $buildtrees 'vulkan-headers/src') -Directory | Where-Object {
    $header = Join-Path $_.FullName 'include/vulkan/vulkan.hpp'
    (Test-Path -LiteralPath $header) -and (Get-FileHash -LiteralPath $header -Algorithm SHA256).Hash -ceq $installedHppHash
})
if ($headerSources.Count -ne 1) { throw 'The Vulkan-Headers license source could not be tied to the installed Vulkan-Hpp bytes.' }
foreach ($component in @('Vulkan-Headers', 'Vulkan-Hpp')) {
    $noticeDirectory = New-OwnedDirectory ('tools/vulkan-sdk-' + $baseline.Substring(0, 12) + '/Notices/' + $component)
    foreach ($license in @('Apache-2.0', 'MIT')) {
        $upstreamLicense = Join-Path $headerSources[0].FullName "LICENSES/$license.txt"
        if (-not (Test-Path -LiteralPath $upstreamLicense -PathType Leaf)) { throw "Missing complete upstream $component license: $license" }
        Copy-Item -LiteralPath $upstreamLicense -Destination (Join-Path $noticeDirectory "LICENSE-$license.txt") -Force
    }
}
Invoke-Checked (Join-Path $bin 'glslc.exe') @('--version')
$files = foreach ($relative in @('Bin/glslc.exe', 'Include/vulkan/vulkan.h', 'Include/vulkan/vulkan.hpp', 'Lib/vulkan-1.lib',
    'share/spirv-headers/SPIRV-HeadersConfig.cmake')) {
    $path = Join-Path $sdk $relative
    [ordered]@{ path = $relative; bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{ schemaVersion = 1; vcpkgBaseline = $baseline; triplet = 'x64-windows';
    vcpkgExecutableSha256 = (Get-FileHash -LiteralPath $VcpkgExecutable -Algorithm SHA256).Hash.ToLowerInvariant();
    vcpkgStatus = (Get-Content -LiteralPath (Join-Path $installed 'vcpkg/status') -Raw); files = @($files) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $sdk 'build-dependencies.json') -Encoding UTF8
$env:VULKAN_SDK = $sdk
if ($GitHubActions) {
    if (-not $env:GITHUB_ENV) { throw 'GitHubActions requires the runner-provided GITHUB_ENV.' }
    "VULKAN_SDK=$sdk" | Out-File -LiteralPath $env:GITHUB_ENV -Encoding UTF8 -Append
}
Write-Host "Prepared isolated Vulkan build dependencies: $sdk"
