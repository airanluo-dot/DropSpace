# QA only. Never invokes GitHub Actions, cancels CI, edits the production model catalog, or builds a runtime.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedRuntimeManifestSha256,
    [string]$ModelDirectory,
    [ValidateSet('hy-mt2-18-q8')][string[]]$ModelIds = @('hy-mt2-18-q8'),
    [ValidateSet('Avx2')][string]$Variant = 'Avx2',
    [ValidateSet(3072)][int]$MemoryMiB = 3072,
    [ValidateSet('official-plain-per-line-v1')][string]$PromptProfile = 'official-plain-per-line-v1',
    [switch]$DownloadMissing,
    [switch]$ExpandedSuite,
    [switch]$LoadOnly,
    [switch]$AcceptanceSuite,
    [switch]$TestCancellation,
    [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'Use PowerShell 7 in a Windows x64 process.' }
if ($LoadOnly -or $TestCancellation) { throw 'This control runs exactly the predeclared translation cases only.' }
$AcceptanceSuite = $true
if ($ModelIds.Count -ne 1) { throw 'Only one Hy Q8 candidate is permitted.' }
$harness = $PSScriptRoot
$qa = [IO.Path]::GetFullPath((Join-Path $harness '../ai-model-qa'))
$repo = [IO.Path]::GetFullPath((Join-Path $qa '../..'))
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
if (-not $ModelDirectory) { $ModelDirectory = Join-Path $repo '.runtime/models' }
$ModelDirectory = [IO.Path]::GetFullPath($ModelDirectory)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $harness ('results/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
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
$holdoutSource = $null
$holdoutSha256 = $null
if ($AcceptanceSuite) {
    Assert-Hash (Join-Path $qa 'inputs/holdout-original.json') '844e47610bfe4b02165b1db8841b8fadb5453380bf09ebd876f5eb96490fc299'
    $holdoutSource = Join-Path $OutputDirectory 'holdout12.json'
    $holdoutSha256 = '88a30d2ddd52307ab5832b457400c4a50545800147ac6c9e8863dd71f96f8b56'
    Assert-Hash (Join-Path $qa 'inputs/holdout12.json') $holdoutSha256
    Copy-Item (Join-Path $qa 'inputs/holdout12.json') $holdoutSource
    Copy-Item (Join-Path $harness 'protocol.json') (Join-Path $OutputDirectory 'plain-protocol.json')
}
$freshHoldoutSource = $null
if ($ExpandedSuite) {
    Assert-Hash (Join-Path $harness 'fresh-holdout.json') '1db102bea381a5e379343ec95e4331db76c7eea733f900bb2b54e0b5ed63a2a4'
    $freshHoldoutSource = Join-Path $OutputDirectory 'fresh-holdout.json'
    Copy-Item (Join-Path $harness 'fresh-holdout.json') $freshHoldoutSource
    Copy-Item (Join-Path $harness 'expansion-protocol.json') (Join-Path $OutputDirectory 'expansion-protocol.json')
}
$sourceFiles = @(
    'scripts/hy-plain-model-qa/Program.cs',
    'scripts/hy-plain-model-qa/PlainProtocol.cs',
    'scripts/hy-plain-model-qa/protocol.json',
    'scripts/hy-plain-model-qa/expansion-protocol.json',
    'scripts/hy-plain-model-qa/fresh-holdout.json',
    'scripts/hy-plain-model-qa/Run-WindowsHyPlainQa.ps1',
    'scripts/ai-model-qa/profiles/minimal-target-only.json',
    'src/DropSpace.Infrastructure/Lyrics/LyricsTranslationCoordinator.cs',
    'src/DropSpace.Infrastructure/Lyrics/LyricsCache.cs',
    'src/DropSpace.Infrastructure/Lyrics/AiLyricsCache.cs',
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
[ordered]@{ utc=(Get-Date).ToUniversalTime().ToString('o'); os=$os.Caption; osVersion=$os.Version; freePhysicalKiB=$os.FreePhysicalMemory; totalVisibleKiB=$os.TotalVisibleMemorySize; cpu=$cpu; variant=$Variant; promptProfile=$PromptProfile; cpuQualification=$cpuQualification; memoryMiB=$MemoryMiB; runtimeManifestSha256=$ExpectedRuntimeManifestSha256; sourceCommit=$manifest.sourceCommit; note='Read-only host observation; no unrelated processes stopped.' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'environment.json') -Encoding utf8
if ([long]$os.FreePhysicalMemory -lt (($MemoryMiB + 1024) * 1024L)) { Write-Warning 'Host free RAM is below the selected job budget plus1GiB; preserve this as possible resource-pressure evidence. The job cap is unchanged.' }
$project = Join-Path $harness 'HyPlainQa.csproj'
& dotnet restore $project -p:RestoreLockedMode=true *> (Join-Path $OutputDirectory 'restore.log')
if ($LASTEXITCODE -ne 0) { throw "QA locked restore failed. See $OutputDirectory/restore.log" }
& dotnet build $project --no-restore -c Release -m:1 -p:UseSharedCompilation=false *> (Join-Path $OutputDirectory 'build.log')
if ($LASTEXITCODE -ne 0) { throw "QA compile failed. See $OutputDirectory/build.log" }
$dll = Join-Path $harness 'bin/Release/net10.0/HyPlainQa.dll'
& dotnet $dll --self-test
if ($LASTEXITCODE -ne 0) { throw 'Plain protocol checks failed' }
$summary = @()
foreach ($id in $ModelIds) {
    if (Test-Path -LiteralPath $cleanupGuard) { throw 'Previous candidate cleanup was not confirmed; remaining models are blocked.' }
    $model = @($candidates.models | Where-Object id -CEQ $id)
    if ($model.Count -ne 1) { throw "Missing/duplicate pinned candidate: $id" }
    $model = $model[0]
    if ($model.url -notmatch '^https://huggingface\.co/(Qwen|ggml-org|ibm-granite|tencent)/[^/]+/resolve/[0-9a-f]{40}/[^/]+\.gguf$') { throw 'Unrecognized candidate URL.' }
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
    $config = [ordered]@{ sourceSha=$env:GITHUB_SHA; runId=$env:GITHUB_RUN_ID; runAttempt=$env:GITHUB_RUN_ATTEMPT; runtimeBuildSourceSha='61596fcb6e757de2e6496613be3791a38142e04d'; runtimeArtifactId=11210435635; runtimeManifestSha256=$ExpectedRuntimeManifestSha256; protocolSha256=(Get-FileHash (Join-Path $harness 'protocol.json') -Algorithm SHA256).Hash.ToLowerInvariant(); sourceFixtureSha256=$candidates.sourceFixtureSha256; modelId=$id; model=$path; modelBytes=$model.bytes; modelSha256=$model.sha256; executable=$exe; executableSha256=$component.sha256; tokenizer=$tokenizer; tokenizerSha256=$manifest.tokenizer.sha256; source=(Join-Path $OutputDirectory 'source48.json'); output=$out; memoryMiB=$MemoryMiB; evaluationMode=$(if ($ExpandedSuite) {'plain-expanded'} else {'plain-control'}); freshHoldoutSource=$freshHoldoutSource; freshHoldoutSha256=$(if ($ExpandedSuite) {'1db102bea381a5e379343ec95e4331db76c7eea733f900bb2b54e0b5ed63a2a4'} else {$null}); expansionProtocolSha256=$(if ($ExpandedSuite) {(Get-FileHash (Join-Path $harness 'expansion-protocol.json') -Algorithm SHA256).Hash.ToLowerInvariant()} else {$null}); holdoutSource=$holdoutSource; holdoutSha256=$holdoutSha256; promptProfile=$PromptProfile; outputSchema='host-mapped-id-text-v1'; loadOnly=[bool]$LoadOnly; testCancellation=[bool]$TestCancellation }
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
    $summary += [ordered]@{ modelId=$id; promptProfile=$PromptProfile; harnessExit=$code; results=(Join-Path $out 'results.json'); semanticStatus='PENDING SEMANTIC REVIEW; record actual reviewer identity; no release approval by exit code.' }
    $summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
    Write-Host "$id completed harness exit $code. Evidence: $out"
}
Write-Host "All scheduled probes ended. No CI/workflow action was performed. Review $OutputDirectory/summary.json and native stdout/stderr."

if (@($summary | Where-Object harnessExit -NE 0).Count -gt 0) { exit 2 }
