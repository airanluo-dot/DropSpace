param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The shipping local AI runtime must be built and verified on Windows x64.' }

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit = '7fe450e19305b828c199d602c23a8337aaa1f03b'
$source = Join-Path $root 'artifacts/ai-runtime-source'
$build = Join-Path $root 'artifacts/ai-runtime-build'
$output = Join-Path $root 'artifacts/ai-runtime/win-x64'

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

# A fresh source tree and CMake cache prevent cached options or local source changes from leaking
# into the trust manifest. Fetch only the immutable official source; never run a user-supplied EXE.
foreach ($directory in @($source, $build, $output)) {
    if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
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
    '-S', $source, '-A', 'x64',
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
foreach ($variant in @('baseline', 'avx2')) {
    $variantBuild = Join-Path $build $variant
    $simd = if ($variant -eq 'avx2') { 'ON' } else { 'OFF' }
    Invoke-Checked 'cmake' ($configure + @('-B', $variantBuild,
        "-DGGML_AVX=$simd", "-DGGML_AVX2=$simd", "-DGGML_FMA=$simd", "-DGGML_F16C=$simd"))
    Invoke-Checked 'cmake' @('--build', $variantBuild, '--config', 'Release', '--target', 'llama-completion', '--parallel', '4')
    if (@(Get-ChildItem (Join-Path $variantBuild 'bin/Release') -Filter '*.dll' -File).Count -ne 0) {
        throw 'The CPU runtime unexpectedly requires a bundled native DLL.'
    }
    $builtExe = Join-Path $variantBuild 'bin/Release/llama-completion.exe'
    if (-not (Test-Path $builtExe -PathType Leaf)) { throw 'The fixed source did not produce llama-completion.exe.' }
    $name = if ($variant -eq 'avx2') { 'llama-completion-avx2.exe' } else { 'llama-completion.exe' }
    Copy-Item $builtExe (Join-Path $output $name)
}
$executable = Join-Path $output 'llama-completion.exe'
$optimized = Join-Path $output 'llama-completion-avx2.exe'
# The completion-only build deliberately disables llama-app, the target that normally
# generates license.cpp. Gather pinned source notices directly instead of relying on it.
$requiredNotices = @('LICENSE', 'vendor/cpp-httplib/LICENSE', 'licenses/LICENSE-jsonhpp')
foreach ($notice in $requiredNotices) {
    if (-not (Test-Path (Join-Path $source $notice) -PathType Leaf)) { throw "Missing required runtime notice: $notice" }
}
$notices = Get-ChildItem $source -Recurse -File |
    Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING)([.-].*)?$' -and $_.FullName -notmatch '[\\/]\.git[\\/]' } |
    Sort-Object FullName
$licenseText = @('Notices from the pinned llama.cpp source tree. Optional unlinked components may also be listed.')
foreach ($notice in $notices) {
    $relative = [IO.Path]::GetRelativePath($source, $notice.FullName)
    $licenseText += "===== $relative =====`n" + (Get-Content $notice.FullName -Raw)
}
$licenseText -join "`n`n" | Set-Content (Join-Path $output 'LICENSE-llama.cpp') -Encoding utf8
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
    avx2 = [ordered]@{
        executable = 'llama-completion-avx2.exe'
        sha256 = (Get-FileHash $optimized -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item $optimized).Length
    }
    build = [ordered]@{ cpuOnly = $true; sharedLibraries = $false; dynamicBackends = $false; server = $false; subprocess = $false; openssl = $false; openmp = $false }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'runtime-manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Test-AiLyricsRuntime.ps1') -RuntimeDirectory $output
Write-Host "Verified embedded runtime payload: $output"
