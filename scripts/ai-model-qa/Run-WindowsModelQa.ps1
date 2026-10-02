# QA only. Never invokes GitHub Actions, cancels CI, edits the production model catalog, or builds a runtime.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedRuntimeManifestSha256,
    [string]$ModelDirectory,
    [ValidateSet('qwen3-17-q4','qwen3-17-q8','granite33-2-q4')][string[]]$ModelIds = @('qwen3-17-q4','granite33-2-q4'),
    [ValidateSet('Baseline','Avx2')][string]$Variant = 'Baseline',
    [ValidateSet(1536,3072)][int]$MemoryMiB = 3072,
    [switch]$DownloadMissing,
    [switch]$LoadOnly,
    [switch]$TestCancellation,
    [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'Use PowerShell 7 in a Windows x64 process.' }
$qa = $PSScriptRoot
$repo = [IO.Path]::GetFullPath((Join-Path $qa '../..'))
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
if (-not $ModelDirectory) { $ModelDirectory = Join-Path $repo '.runtime/models' }
$ModelDirectory = [IO.Path]::GetFullPath($ModelDirectory)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $qa ('results/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; prior evidence is never overwritten.' }
New-Item $OutputDirectory -ItemType Directory | Out-Null
# Persist setup/download/hash failures too; do not leave only a misleading empty result folder.
trap {
    $failure = $_
    try {
        if ($OutputDirectory -and (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
            [ordered]@{ technicalStatus='FAILED OR INCOMPLETE'; semanticStatus='Not evaluated'; error=$failure.ToString(); details=$failure.ScriptStackTrace } |
                ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'setup-failure.json') -Encoding utf8
        }
    } catch { }
    Write-Error -ErrorRecord $failure -ErrorAction Continue
    exit 1
}
New-Item $ModelDirectory -ItemType Directory -Force | Out-Null
function Assert-PlainPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse path is not allowed: $Path" }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
}
function Assert-Hash([string]$Path,[string]$Hash,[long]$Bytes = -1) {
    Assert-PlainPath $Path
    if ($Bytes -ge 0 -and (Get-Item -LiteralPath $Path).Length -ne $Bytes) { throw "Byte count mismatch: $Path" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Hash) { throw "SHA256 mismatch: $Path" }
}
Assert-PlainPath $qa
Assert-PlainPath $ModelDirectory
$cleanupGuard = Join-Path $ModelDirectory 'inference-cleanup-unresolved.json'
if (Test-Path -LiteralPath $cleanupGuard) { throw 'Previous candidate cleanup was not confirmed. Stop this diagnostic sequence; do not overlap native inference.' }
Assert-PlainPath $OutputDirectory
$manifestPath = Join-Path $runtime 'runtime-manifest.json'
Assert-Hash $manifestPath $ExpectedRuntimeManifestSha256
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.runtimeId -cne 'llama-cpp-v0.5.0-cpu-win-x64' -or $manifest.sourceCommit -cne '7fe450e19305b828c199d602c23a8337aaa1f03b') { throw 'Unexpected trusted runtime manifest identity.' }
if ($manifest.executable -cne 'llama-completion.exe' -or $manifest.tokenizer.executable -cne 'llama-tokenize.exe') { throw 'Unexpected runtime component names.' }
$cpuQualification = [ordered]@{ avx2=[System.Runtime.Intrinsics.X86.Avx2]::IsSupported; fma=[System.Runtime.Intrinsics.X86.Fma]::IsSupported; x86=[System.Runtime.Intrinsics.X86.X86Base]::IsSupported; f16c=$false }
if ($cpuQualification.x86) { $cpuFeatures=[System.Runtime.Intrinsics.X86.X86Base]::CpuId(1,0); $cpuQualification.f16c=($cpuFeatures.Item3 -band (1 -shl 29)) -ne 0 }
$cpuQualification | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'cpu-qualification.json') -Encoding utf8
if ($Variant -eq 'Avx2') {
    if (-not [System.Runtime.Intrinsics.X86.Avx2]::IsSupported -or -not [System.Runtime.Intrinsics.X86.Fma]::IsSupported -or -not [System.Runtime.Intrinsics.X86.X86Base]::IsSupported) { throw 'Host lacks required AVX2/FMA CPU features.' }
    $cpuFeatures = [System.Runtime.Intrinsics.X86.X86Base]::CpuId(1,0)
    if (($cpuFeatures.Item3 -band (1 -shl 29)) -eq 0) { throw 'Host lacks required F16C CPU feature.' }
}
$component = if ($Variant -eq 'Avx2') { $manifest.avx2 } else { $manifest }
if ($Variant -eq 'Avx2' -and $component.executable -cne 'llama-completion-avx2.exe') { throw 'Unexpected AVX2 component name.' }
$exe = Join-Path $runtime $component.executable
$tokenizer = Join-Path $runtime $manifest.tokenizer.executable
Assert-Hash $exe $component.sha256 $component.bytes
Assert-Hash $tokenizer $manifest.tokenizer.sha256 $manifest.tokenizer.bytes
$candidates = Get-Content -LiteralPath (Join-Path $qa 'candidates.json') -Raw | ConvertFrom-Json
Assert-Hash (Join-Path $qa 'inputs/source48.json') $candidates.sourceFixtureSha256
Copy-Item (Join-Path $qa 'inputs/source48.json') (Join-Path $OutputDirectory 'source48.json')
Assert-Hash (Join-Path $OutputDirectory 'source48.json') $candidates.sourceFixtureSha256
$sourceFiles = @(
    'src/DropSpace.Core/Lyrics/LyricsTranslationPrompt.cs',
    'src/DropSpace.Core/Lyrics/LyricsTranslationOutput.cs',
    'src/DropSpace.Infrastructure/Lyrics/LocalInferenceProcess.cs',
    'src/DropSpace.Infrastructure/Lyrics/WindowsInferenceProcess.cs',
    'src/DropSpace.Infrastructure/Storage/ReparseSafePathPolicy.cs'
)
$hashes = foreach ($relative in $sourceFiles) { [ordered]@{ path=$relative; sha256=(Get-FileHash (Join-Path $repo $relative) -Algorithm SHA256).Hash.ToLowerInvariant() } }
$hashes | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'production-source-hashes.json') -Encoding utf8
Copy-Item $manifestPath (Join-Path $OutputDirectory 'runtime-manifest.json')
Copy-Item (Join-Path $qa 'candidates.json') (Join-Path $OutputDirectory 'candidates.json')
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors
[ordered]@{ utc=(Get-Date).ToUniversalTime().ToString('o'); os=$os.Caption; osVersion=$os.Version; freePhysicalKiB=$os.FreePhysicalMemory; totalVisibleKiB=$os.TotalVisibleMemorySize; cpu=$cpu; variant=$Variant; cpuQualification=$cpuQualification; memoryMiB=$MemoryMiB; runtimeManifestSha256=$ExpectedRuntimeManifestSha256; sourceCommit=$manifest.sourceCommit; note='Read-only host observation; no unrelated processes stopped.' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'environment.json') -Encoding utf8
if ([long]$os.FreePhysicalMemory -lt (($MemoryMiB + 1024) * 1024L)) { Write-Warning 'Host free RAM is below the selected job budget plus1GiB; preserve this as possible resource-pressure evidence. The job cap is unchanged.' }
$project = Join-Path $qa 'WindowsModelQa.csproj'
& dotnet restore $project -p:RestoreLockedMode=true *> (Join-Path $OutputDirectory 'restore.log')
if ($LASTEXITCODE -ne 0) { throw "QA locked restore failed. See $OutputDirectory/restore.log" }
& dotnet build $project --no-restore -c Release -m:1 -p:UseSharedCompilation=false *> (Join-Path $OutputDirectory 'build.log')
if ($LASTEXITCODE -ne 0) { throw "QA compile failed. See $OutputDirectory/build.log" }
$dll = Join-Path $qa 'bin/Release/net10.0/WindowsModelQa.dll'
$summary = @()
foreach ($id in $ModelIds) {
    if (Test-Path -LiteralPath $cleanupGuard) { throw 'Previous candidate cleanup was not confirmed; remaining models are blocked.' }
    $model = @($candidates.models | Where-Object id -CEQ $id)
    if ($model.Count -ne 1) { throw "Missing/duplicate pinned candidate: $id" }
    $model = $model[0]
    if ($model.url -notmatch '^https://huggingface\.co/(Qwen|ggml-org|ibm-granite)/[^/]+/resolve/[0-9a-f]{40}/[^/]+\.gguf$') { throw 'Unrecognized candidate URL.' }
    $path = Join-Path $ModelDirectory $model.file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        if (-not $DownloadMissing) { throw "Missing $path. Copy the verified existing model or explicitly pass -DownloadMissing." }
        $partial = $path + '.partial'
        if (Test-Path -LiteralPath $partial) { Assert-PlainPath $partial }
        Write-Host "Downloading pinned $id ($($model.bytes) bytes)"
        Invoke-WebRequest -Uri $model.url -OutFile $partial -Resume
        Assert-Hash $partial $model.sha256 $model.bytes
        Move-Item -LiteralPath $partial -Destination $path
    }
    Assert-Hash $path $model.sha256 $model.bytes
    $out = Join-Path $OutputDirectory $id
    New-Item $out -ItemType Directory | Out-Null
    Get-CimInstance Win32_OperatingSystem | Select-Object FreePhysicalMemory,TotalVisibleMemorySize,FreeVirtualMemory,TotalVirtualMemorySize | ConvertTo-Json | Set-Content (Join-Path $out 'host-before.json') -Encoding utf8
    $config = [ordered]@{ modelId=$id; model=$path; modelBytes=$model.bytes; modelSha256=$model.sha256; executable=$exe; executableSha256=$component.sha256; tokenizer=$tokenizer; tokenizerSha256=$manifest.tokenizer.sha256; source=(Join-Path $OutputDirectory 'source48.json'); output=$out; memoryMiB=$MemoryMiB; loadOnly=[bool]$LoadOnly; testCancellation=[bool]$TestCancellation }
    $configPath = Join-Path $out 'qa-config.json'
    $config | ConvertTo-Json -Depth 4 | Set-Content $configPath -Encoding utf8
    & dotnet $dll $configPath *> (Join-Path $out 'console.log')
    $code = $LASTEXITCODE
    $owned = @()
    $resultFile = Join-Path $out 'results.json'
    if (Test-Path -LiteralPath $resultFile) {
        foreach ($entry in (Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json)) {
            if ($entry.PSObject.Properties.Name -contains 'native' -and $entry.native.CleanupCompleted -eq $false) {
                [ordered]@{ modelId=$id; reason='Native cleanup unresolved; further candidates blocked'; evidence=$resultFile } |
                    ConvertTo-Json | Set-Content $cleanupGuard -Encoding utf8
            }
            if ($entry.PSObject.Properties.Name -contains 'native' -and $null -ne $entry.native.ProcessId) {
                $nativePid = [int]$entry.native.ProcessId
                $observed = Get-Process -Id $nativePid -ErrorAction SilentlyContinue
                if ($null -ne $observed) { try { $observedPath=$observed.Path } catch { $observedPath=$null }; $owned += [ordered]@{ pid=$nativePid; path=$observedPath; matchesExpectedExecutable=($observedPath -ieq $exe -or $observedPath -ieq $tokenizer); note='Observation only; PID may have been reused; nothing was killed.' } }
            }
        }
    }
    ConvertTo-Json -InputObject @($owned) -Depth 4 | Set-Content (Join-Path $out 'owned-process-observation.json') -Encoding utf8
    Get-CimInstance Win32_OperatingSystem | Select-Object FreePhysicalMemory,TotalVisibleMemorySize,FreeVirtualMemory,TotalVirtualMemorySize | ConvertTo-Json | Set-Content (Join-Path $out 'host-after.json') -Encoding utf8
    $summary += [ordered]@{ modelId=$id; harnessExit=$code; results=(Join-Path $out 'results.json'); semanticStatus='Human review required; never a release gate by exit code alone.' }
    $summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
    Write-Host "$id completed harness exit $code. Evidence: $out"
}
Write-Host "All scheduled probes ended. No CI/workflow action was performed. Review $OutputDirectory/summary.json and native stdout/stderr."

if (@($summary | Where-Object harnessExit -NE 0).Count -gt 0) { exit 2 }
