param(
    [Parameter(Mandatory)][string]$VerifiedSourceDirectory,
    [Parameter(Mandatory)][string]$CudaToolkitDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+(;[0-9]+)*$')][string]$CudaArchitectures,
    [ValidateSet('Ninja Multi-Config', 'Visual Studio 17 2022', 'Visual Studio 18 2026')]
    [string]$Generator = 'Ninja Multi-Config',
    # Never run a native startup check during a compile-only task.
    [switch]$ValidateStartup
)
# Explicit producer only. Never installs a driver, downloads a model, builds CPU/Vulkan,
# dispatches a paid GPU runner, or writes the shipping runtime directory.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'CUDA experiment packaging requires Windows x64 and an existing CUDA 12 toolkit.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = [IO.Path]::GetFullPath($VerifiedSourceDirectory)
$toolkit = [IO.Path]::GetFullPath($CudaToolkitDirectory)
$commit = '7fe450e19305b828c199d602c23a8337aaa1f03b'
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed: $LASTEXITCODE" }
}
$actual = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -cne $commit) { throw 'CUDA must reuse the exact pinned official engine.' }
$remote = (& git -C $source remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $remote -cne 'https://github.com/ggml-org/llama.cpp.git') { throw 'Unexpected engine origin.' }
$dirty = @(& git -C $source status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $dirty.Count) { throw 'Reused engine source must be clean, including untracked files.' }
Invoke-Checked 'git' @('-C', $source, 'fsck', '--no-reflogs')
if (-not (Test-Path (Join-Path $toolkit 'bin/nvcc.exe'))) { throw 'CUDA toolkit is unavailable; no installation is attempted.' }
$version = & (Join-Path $toolkit 'bin/nvcc.exe') --version
$toolkitVersion = [regex]::Match(($version -join "`n"), 'release (?<major>[0-9]+)\.(?<minor>[0-9]+)')
if ($LASTEXITCODE -ne 0 -or -not $toolkitVersion.Success -or
    [int]$toolkitVersion.Groups['major'].Value -ne 12 -or [int]$toolkitVersion.Groups['minor'].Value -lt 3) {
    throw 'Experiment v1 requires CUDA 12.3 or later in the CUDA 12 ABI.'
}
# Fresh isolated paths; preserve all previous builds. No CPU/Vulkan rebuilding or payload overwrite.
$build = Join-Path $root ('artifacts/cuda-experiment-build-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $build 'payload'
New-Item $output -ItemType Directory -Force | Out-Null
$inputs = @('tools/plain-lyrics-helper/main.cpp', 'tools/plain-lyrics-helper/gpu-policy.h',
    'tools/cuda-lyrics-helper/adapt_worker.py', 'tools/cuda-lyrics-helper/cuda-device.h', 'tools/cuda-lyrics-helper/CMakeLists.txt')
$before = @($inputs | ForEach-Object { (Get-FileHash (Join-Path $root $_) -Algorithm SHA256).Hash })
$configure = @('-S', (Join-Path $root 'tools/cuda-lyrics-helper'), '-B', $build, '-G', $Generator,
    "-DLLAMA_SOURCE=$source", "-DCUDAToolkit_ROOT=$toolkit", "-DCMAKE_CUDA_COMPILER=$(Join-Path $toolkit 'bin/nvcc.exe')",
    "-DCMAKE_CUDA_ARCHITECTURES=$CudaArchitectures", '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded')
if ($Generator -ne 'Ninja Multi-Config') { $configure += @('-A', 'x64') }
Invoke-Checked 'cmake' $configure
Invoke-Checked 'cmake' @('--build', $build, '--config', 'Release', '--target', 'plain-lyrics-worker-cuda', '--parallel', '4')
Copy-Item (Join-Path $build 'bin/Release/plain-lyrics-worker-cuda.exe') $output
# Pinned ggml links Windows cuBLAS dynamically even with GGML_STATIC=ON.
# Treat both toolkit DLLs as independently hashed components, never search PATH for them.
foreach ($name in @('cublas64_12.dll', 'cublasLt64_12.dll')) {
    Copy-Item (Join-Path $toolkit "bin/$name") (Join-Path $output $name)
}
for ($i = 0; $i -lt $inputs.Count; $i++) {
    if ((Get-FileHash (Join-Path $root $inputs[$i]) -Algorithm SHA256).Hash -cne $before[$i]) { throw 'Worker inputs changed during CUDA build.' }
}
if (@(& git -C $source status --porcelain --untracked-files=all).Count) { throw 'Engine changed during CUDA build.' }
$files = @('plain-lyrics-worker-cuda.exe', 'cublas64_12.dll', 'cublasLt64_12.dll') | ForEach-Object {
    $path = Join-Path $output $_
    [ordered]@{ name = $_; sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item $path).Length }
}
$manifest = [ordered]@{
    schemaVersion = 1; runtimeId = 'llama-cpp-v0.5.0-cuda12-win-x64-experiment-v1'; backend = 'cuda'
    sourceRepository = 'https://github.com/ggml-org/llama.cpp'; sourceCommit = $commit
    protocol = 1; profile = 'hy-q8-plain-resident-v1'
    workerSourceSha256 = (Get-FileHash (Join-Path $build 'cuda-worker.cpp') -Algorithm SHA256).Hash.ToLowerInvariant()
    files = @($files)
    build = [ordered]@{ architectures = $CudaArchitectures; toolkitVersion = $version -join "`n";
        sharedLibraries = $false; dynamicBackends = $false; vulkan = $false; hip = $false;
        cudaRuntime = 'static'; cublas = 'bundled-cuda12'; pdl = 'pinned-upstream-default'; inputSha256 = $before }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'cuda-runtime-manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Collect-AiRuntimeNotices.ps1') -Source $source -OutputPath (Join-Path $build 'LICENSE-llama.cpp')
Copy-Item (Join-Path $toolkit 'EULA.txt') (Join-Path $build 'LICENSE-CUDA.txt')
# Fail if PE imports imply a missing runtime; review imports before embedding these three components.
if (-not (Get-Command dumpbin -ErrorAction SilentlyContinue)) { throw 'MSVC dumpbin must be available for CUDA dependency review.' }
foreach ($component in $files) {
    $imports = & dumpbin /dependents (Join-Path $output $component.name)
    if ($LASTEXITCODE -ne 0) { throw 'CUDA PE dependency inspection failed.' }
    $imports | Set-Content (Join-Path $build ($component.name + '-imports.txt'))
    $unexpected = @($imports | ForEach-Object {
        if ($_ -match '^\s+(?<dll>[^\s]+\.dll)\s*$') { $Matches.dll }
    } | Where-Object { $_ -notmatch '^(?i:kernel32|user32|advapi32|shell32|ole32|ws2_32|bcrypt|crypt32|ntdll|version|nvcuda|cublas64_12|cublasLt64_12)\.dll$' -and $_ -notmatch '^(?i:api-ms-win-)' })
    if ($unexpected.Count) { throw "Unreviewed CUDA dependencies in $($component.name): $($unexpected -join ', ')" }
}
if ($ValidateStartup) {
    $smoke = & (Join-Path $output 'plain-lyrics-worker-cuda.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'CUDA component startup failed; it is not a Vulkan failure.' }
    $identity = $smoke | ConvertFrom-Json
    if ($identity.backend -cne 'cuda' -or $identity.componentId -cne $manifest.runtimeId -or $identity.profile -cne $manifest.profile) {
        throw 'CUDA component identity handshake failed.'
    }
}
Write-Host "Isolated CUDA experiment payload (not published or embedded): $output"
