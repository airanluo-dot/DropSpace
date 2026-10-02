param([string]$RuntimeDirectory = '', [string]$ModelPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native AI runtime verification requires Windows.' }
if (-not $RuntimeDirectory) { $RuntimeDirectory = Join-Path $PSScriptRoot '../artifacts/ai-runtime/win-x64' }
$RuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
$manifest = Get-Content (Join-Path $RuntimeDirectory 'runtime-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.runtimeId -cne 'llama-cpp-v0.5.0-cpu-win-x64' -or
    $manifest.sourceRepository -cne 'https://github.com/ggml-org/llama.cpp' -or
    $manifest.sourceCommit -cne '7fe450e19305b828c199d602c23a8337aaa1f03b' -or
    $manifest.executable -cne 'llama-completion.exe') { throw 'Unexpected runtime provenance.' }
if ($manifest.build.cpuOnly -or -not $manifest.build.legacyCompletionCpuOnly -or $manifest.build.residentGpuBackend -cne 'vulkan' -or $manifest.build.sharedLibraries -or $manifest.build.dynamicBackends -or
    $manifest.build.server -or $manifest.build.subprocess -or $manifest.build.openssl -or $manifest.build.openmp) {
    throw 'Unexpected native runtime build features.'
}
if (@(Get-ChildItem $RuntimeDirectory -Directory).Count -ne 0) { throw 'Unexpected runtime payload subdirectory.' }
$names = @(Get-ChildItem $RuntimeDirectory -File | ForEach-Object Name | Sort-Object)
$expected = @('LICENSE-llama.cpp', 'llama-completion.exe', 'llama-completion-avx2.exe', 'llama-tokenize.exe', 'plain-lyrics-worker.exe', 'plain-lyrics-worker-avx2.exe', 'plain-lyrics-worker-vulkan.exe', 'runtime-manifest.json') | Sort-Object
if (Compare-Object $names $expected) { throw 'The runtime payload contains missing or unexpected files.' }
if ($manifest.avx2.executable -cne 'llama-completion-avx2.exe') { throw 'Unexpected optimized runtime executable.' }
if ($manifest.tokenizer.executable -cne 'llama-tokenize.exe') { throw 'Unexpected tokenizer executable.' }
if ($manifest.resident.protocol -ne 1 -or $manifest.resident.profile -cne 'hy-q8-plain-resident-v1' -or
    $manifest.resident.sourceSha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid resident worker provenance.' }
foreach ($variant in @($manifest, $manifest.avx2, $manifest.tokenizer, $manifest.resident.cpu, $manifest.resident.avx2, $manifest.resident.vulkan)) {
    $exe = Join-Path $RuntimeDirectory $variant.executable
    if ((Get-Item $exe).Length -ne $variant.bytes -or (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant() -cne $variant.sha256) {
        throw 'Native runtime size/SHA256 verification failed.'
    }
    $stream = [IO.File]::OpenRead($exe)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Not a Windows executable.' }
        $stream.Position = 0x3c
        $stream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Runtime is not a native Windows x64 PE.' }
    } finally { $reader.Dispose() }
}
$exe = Join-Path $RuntimeDirectory 'llama-completion.exe'
$version = & $exe --version 2>&1
if ($LASTEXITCODE -ne 0 -or ($version -join "`n") -notmatch '0\.5\.0') { throw 'Native runtime version smoke failed.' }

if ($ModelPath) {
    if (-not (Test-Path $ModelPath -PathType Leaf)) { throw 'Native smoke model is missing.' }
    # Model inference must use the actual production plaintext backend and Windows Job ownership,
    # never a parallel raw Process.Start path or the retired JSON grammar profile.
    $previousModel = $env:DROPSPACE_AI_SMOKE_MODEL
    $previousRuntime = $env:DROPSPACE_AI_SMOKE_RUNTIME
    try {
        $env:DROPSPACE_AI_SMOKE_MODEL = [IO.Path]::GetFullPath($ModelPath)
        $env:DROPSPACE_AI_SMOKE_RUNTIME = $RuntimeDirectory
        $project = Join-Path $PSScriptRoot '../tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj'
        & dotnet test $project --filter 'FullyQualifiedName~AiLyricsNativeRuntimeSmokeTests.PinnedWindowsRuntimeTranslatesOriginalLinesAndHonorsCancellation' --logger 'console;verbosity=normal'
        if ($LASTEXITCODE -ne 0) { throw 'Actual production plaintext native smoke failed.' }
    } finally {
        $env:DROPSPACE_AI_SMOKE_MODEL = $previousModel
        $env:DROPSPACE_AI_SMOKE_RUNTIME = $previousRuntime
    }
    Write-Host 'Actual plaintext Beta model/backend and native cancellation checks passed.'
}
Write-Host 'Pinned Windows runtime provenance, SHA256, x64 image, payload, and startup verified.'
