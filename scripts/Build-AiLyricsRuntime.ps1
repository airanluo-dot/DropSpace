param(
    [ValidateSet('Ninja Multi-Config', 'Visual Studio 18 2026', 'Visual Studio 17 2022')]
    [string]$Generator = 'Ninja Multi-Config',
    [string]$BuildDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-AiRuntimeNotices.ps1')
if (-not $IsWindows) { throw 'The shipping local AI runtime must be built and verified on Windows x64.' }

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit = '7fe450e19305b828c199d602c23a8337aaa1f03b'
$source = Join-Path $root 'artifacts/ai-runtime-source'
$build = if ($BuildDirectory) { [IO.Path]::GetFullPath($BuildDirectory) } else { Join-Path $root 'artifacts/ai-runtime-build' }
if ($BuildDirectory -and (Test-Path -LiteralPath $build)) { throw 'An explicitly supplied native build directory must be fresh; existing contents are preserved.' }
$output = Join-Path $root 'artifacts/ai-runtime/win-x64'
$helperInputs = @('tools/plain-lyrics-helper/CMakeLists.txt', 'tools/plain-lyrics-helper/gpu-policy.h', 'tools/plain-lyrics-helper/main.cpp')
$helperInputIdentities = @{}
foreach ($inputPath in $helperInputs) {
    $helperInputIdentities[$inputPath] = (Get-FileHash -LiteralPath (Join-Path $root $inputPath) -Algorithm SHA256).Hash
}

function Assert-OwnedBuildDirectory([string]$Directory) {
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
    $absolute = [IO.Path]::GetFullPath($Directory)
    $allowedRoot = $artifactsRoot
    if ($BuildDirectory -and $absolute -ceq $build) {
        $temporaryRoots = @([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar),
            [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp')))
        $matchingRoots = @($temporaryRoots | Where-Object { $absolute.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
        if ($matchingRoots.Count -gt 0) { $allowedRoot = $matchingRoots[0] }
    }
    if (-not $absolute.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime build directory escaped repository artifacts and the approved temporary directories.'
    }
    for ($ancestor = $absolute; $ancestor -and $ancestor.Length -ge $allowedRoot.Length; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Runtime build directories must not traverse a reparse point: $ancestor"
            }
        }
    }
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

if ($Generator -eq 'Ninja Multi-Config') {
    # VS/MSBuild's shader-generator subproject fails under deep checkout paths.
    # Ninja avoids that MAX_PATH restriction without changing global Windows policy.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio discovery is unavailable.' }
    $visualStudio = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $visualStudio) { throw 'The x64 MSVC build tools are unavailable.' }
    Import-Module (Join-Path $visualStudio 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $visualStudio -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
    foreach ($toolDirectory in @('Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin', 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja')) {
        $toolPath = Join-Path $visualStudio $toolDirectory
        if (Test-Path -LiteralPath $toolPath -PathType Container) { $env:PATH = $toolPath + [IO.Path]::PathSeparator + $env:PATH }
    }
}

# A fresh source tree and CMake cache prevent cached options or local source changes from leaking
# into the trust manifest. Fetch only the immutable official source; never run a user-supplied EXE.
foreach ($directory in @($source, $build, $output)) {
    Assert-OwnedBuildDirectory $directory
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    New-Item $directory -ItemType Directory -Force | Out-Null
}
Invoke-Checked 'git' @('init', $source)
Invoke-Checked 'git' @('-C', $source, 'remote', 'add', 'origin', 'https://github.com/ggml-org/llama.cpp.git')
Invoke-Checked 'git' @('-C', $source, '-c', 'fetch.fsckObjects=true', 'fetch', '--depth=1', 'origin', $commit)
Invoke-Checked 'git' @('-C', $source, 'checkout', '--detach', 'FETCH_HEAD')
$actual = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -cne $commit) { throw 'Runtime source commit verification failed.' }
Invoke-Checked 'git' @('-C', $source, 'fsck', '--no-reflogs')

$configure = @(
    '-S', (Join-Path $root 'tools/plain-lyrics-helper'), '-G', $Generator, "-DLLAMA_SOURCE=$source",
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',
    '-DBUILD_SHARED_LIBS=OFF', '-DLLAMA_BUILD_IS_DEV=OFF',
    '-DLLAMA_BUILD_TESTS=OFF', '-DLLAMA_BUILD_EXAMPLES=OFF', '-DLLAMA_BUILD_TOOLS=ON',
    '-DLLAMA_BUILD_SERVER=OFF', '-DLLAMA_BUILD_APP=OFF', '-DLLAMA_BUILD_UI=OFF',
    '-DLLAMA_OPENSSL=OFF', '-DLLAMA_SUBPROCESS=OFF', '-DLLAMA_LLGUIDANCE=OFF',
    '-DLLAMA_BUILD_BORINGSSL=OFF', '-DLLAMA_BUILD_LIBRESSL=OFF',
    '-DGGML_NATIVE=OFF', '-DGGML_CPU=ON', '-DGGML_BACKEND_DL=OFF',
    '-DGGML_CPU_ALL_VARIANTS=OFF', '-DGGML_OPENMP=OFF', '-DGGML_OPENMP_FETCH=OFF',
    '-DGGML_CUDA=OFF', '-DGGML_HIP=OFF', '-DGGML_VULKAN=OFF', '-DGGML_SYCL=OFF',
    '-DGGML_OPENCL=OFF', '-DGGML_METAL=OFF', '-DGGML_BLAS=OFF', '-DGGML_RPC=OFF',
    '-DGGML_MUSA=OFF', '-DGGML_WEBGPU=OFF', '-DGGML_OPENVINO=OFF', '-DGGML_HEXAGON=OFF',
    '-DGGML_ZENDNN=OFF', '-DGGML_VIRTGPU=OFF', '-DGGML_VIRTGPU_BACKEND=OFF',
    '-DGGML_SSE42=OFF', '-DGGML_BMI2=OFF', '-DGGML_AVX512=OFF',
    '-DFETCHCONTENT_FULLY_DISCONNECTED=ON'
)
if ($Generator -ne 'Ninja Multi-Config') { $configure += @('-A', 'x64') }
if (-not $env:VULKAN_SDK -or -not (Test-Path (Join-Path $env:VULKAN_SDK 'Bin/glslc.exe'))) {
    throw 'Resident GPU runtime requires Vulkan build dependencies (glslc, headers, import library). Run scripts/Prepare-VulkanBuildDependencies.ps1 first. No driver is installed by either script.'
}
foreach ($variant in @('vulkan', 'baseline', 'avx2')) {
    $variantBuild = Join-Path $build $variant
    $simd = if ($variant -eq 'avx2') { 'ON' } else { 'OFF' }
    $vulkan = if ($variant -eq 'vulkan') { 'ON' } else { 'OFF' }
    Invoke-Checked 'cmake' ($configure + @('-B', $variantBuild,
        "-DGGML_AVX=$simd", "-DGGML_AVX2=$simd", "-DGGML_FMA=$simd", "-DGGML_F16C=$simd", "-DGGML_VULKAN=$vulkan"))
    Invoke-Checked 'cmake' @('--build', $variantBuild, '--config', 'Release', '--target', 'plain-lyrics-worker', '--parallel', '4')
    $workerName = if ($variant -eq 'baseline') { 'plain-lyrics-worker.exe' } else { "plain-lyrics-worker-$variant.exe" }
    Copy-Item (Join-Path $variantBuild 'bin/Release/plain-lyrics-worker.exe') (Join-Path $output $workerName)
    if (@(Get-ChildItem (Join-Path $variantBuild 'bin/Release') -Filter '*.dll' -File).Count -ne 0) { throw 'The resident runtime unexpectedly requires a bundled native DLL.' }
    if ($variant -eq 'vulkan') { continue }
    Invoke-Checked 'cmake' @('--build', $variantBuild, '--config', 'Release', '--target', 'llama-completion', '--parallel', '4')
    if (@(Get-ChildItem (Join-Path $variantBuild 'bin/Release') -Filter '*.dll' -File).Count -ne 0) {
        throw 'The CPU runtime unexpectedly requires a bundled native DLL.'
    }
    if ($variant -eq 'baseline') {
        Invoke-Checked 'cmake' @('--build', $variantBuild, '--config', 'Release', '--target', 'llama-tokenize', '--parallel', '4')
        Copy-Item (Join-Path $variantBuild 'bin/Release/llama-tokenize.exe') (Join-Path $output 'llama-tokenize.exe')
    }
    $builtExe = Join-Path $variantBuild 'bin/Release/llama-completion.exe'
    if (-not (Test-Path $builtExe -PathType Leaf)) { throw 'The fixed source did not produce llama-completion.exe.' }
    $name = if ($variant -eq 'avx2') { 'llama-completion-avx2.exe' } else { 'llama-completion.exe' }
    Copy-Item $builtExe (Join-Path $output $name)
}
$executable = Join-Path $output 'llama-completion.exe'
$optimized = Join-Path $output 'llama-completion-avx2.exe'
foreach ($inputPath in $helperInputs) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $inputPath) -Algorithm SHA256).Hash -cne $helperInputIdentities[$inputPath]) {
        throw 'Resident worker source changed during the build. Discard this incomplete build and rebuild frozen inputs before generating a trust manifest.'
    }
}
# The completion-only build deliberately disables llama-app, the target that normally
# generates license.cpp. Gather pinned source notices directly instead of relying on it.
& (Join-Path $PSScriptRoot 'Collect-AiRuntimeNotices.ps1') -Source $source -OutputPath (Join-Path $output 'LICENSE-llama.cpp') -VulkanSdk $env:VULKAN_SDK
# The application embeds this build-produced manifest alongside the exact EXE; the manifest is not
# accepted from a download or cache folder. Toolchain changes can change the binary SHA256.
$manifest = [ordered]@{
    schemaVersion = 1
    runtimeId = 'llama-cpp-v0.5.0-cpu-win-x64'
    sourceRepository = 'https://github.com/ggml-org/llama.cpp'
    sourceCommit = $commit
    executable = 'llama-completion.exe'
    sha256 = (Get-FileHash $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    bytes = (Get-Item $executable).Length
    tokenizer = [ordered]@{
        executable = 'llama-tokenize.exe'
        sha256 = (Get-FileHash (Join-Path $output 'llama-tokenize.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item (Join-Path $output 'llama-tokenize.exe')).Length
    }
    avx2 = [ordered]@{
        executable = 'llama-completion-avx2.exe'
        sha256 = (Get-FileHash $optimized -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item $optimized).Length
    }
    resident = [ordered]@{
        protocol = 1
        profile = 'hy-q8-plain-resident-v1'
        sourceSha256 = ''
        cpu = [ordered]@{ executable = 'plain-lyrics-worker.exe'; sha256 = (Get-FileHash (Join-Path $output 'plain-lyrics-worker.exe') -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item (Join-Path $output 'plain-lyrics-worker.exe')).Length }
        avx2 = [ordered]@{ executable = 'plain-lyrics-worker-avx2.exe'; sha256 = (Get-FileHash (Join-Path $output 'plain-lyrics-worker-avx2.exe') -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item (Join-Path $output 'plain-lyrics-worker-avx2.exe')).Length }
        vulkan = [ordered]@{ executable = 'plain-lyrics-worker-vulkan.exe'; sha256 = (Get-FileHash (Join-Path $output 'plain-lyrics-worker-vulkan.exe') -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item (Join-Path $output 'plain-lyrics-worker-vulkan.exe')).Length }
    }
    build = [ordered]@{ cpuOnly = $false; legacyCompletionCpuOnly = $true; residentGpuBackend = 'vulkan'; sharedLibraries = $false; dynamicBackends = $false; server = $false; subprocess = $false; openssl = $false; openmp = $false }
}
$helperSource = @('CMakeLists.txt', 'gpu-policy.h', 'main.cpp') | ForEach-Object {
    [IO.File]::ReadAllText((Join-Path $root "tools/plain-lyrics-helper/$_")).Replace("`r`n", "`n")
}
$manifest.resident.sourceSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($helperSource -join "`n")))).ToLowerInvariant()
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'runtime-manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Test-AiLyricsRuntime.ps1') -RuntimeDirectory $output
Write-Host "Verified embedded runtime payload: $output"
