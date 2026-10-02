# Finite, read-only production evidence capture. Never grants semantic/release approval.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$ModelPath,
    [Parameter(Mandatory)][ValidateSet('hy-mt2-18-q8-plain-beta', 'hy-mt2-7b-q8-plain-beta')][string]$ModelId,
    [Parameter(Mandatory)][ValidateSet('Baseline', 'Avx2')][string]$Variant,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($OutputDirectory.StartsWith($PSScriptRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Place output outside the harness source directory so snapshots cannot enter compiler source discovery.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output directory already exists. Capture never overwrites or resumes earlier evidence.' }
$captureName = [IO.Path]::GetFileName($OutputDirectory)
if ($captureName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,119}$' -or $captureName.EndsWith('.') -or $captureName -match '^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)') { throw 'Choose a new output directory with a canonical, unique capture name.' }
$logicalRoot = 'scripts/ai-model-qa/evidence/' + $captureName
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
trap {
    $failure = $_
    try {
        if (Test-Path -LiteralPath $OutputDirectory -PathType Container) {
            $failureFile = Join-Path $OutputDirectory 'capture-failure.json'
            if (-not (Test-Path -LiteralPath $failureFile)) {
                [ordered]@{ schemaVersion=1; status='failed-or-incomplete'; error=$failure.ToString(); details=$failure.ScriptStackTrace; semanticStatus='not-evaluated'; releaseApprovalWritten=$false } |
                    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $failureFile -Encoding utf8NoBOM -NoNewline
            }
        }
    } catch { }
    Write-Error -ErrorRecord $failure -ErrorAction Continue
    exit 1
}
function Write-NewJson([string]$Name, [object]$Value) {
    $path = Join-Path $OutputDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Evidence file already exists: $Name" }
    $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $path -Encoding utf8NoBOM -NoNewline
}
function Assert-PlainPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse paths are not permitted: $Path" }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
}
function Get-Identity([string]$Path) {
    Assert-PlainPath $Path
    $item = Get-Item -LiteralPath $Path
    if ($item -isnot [IO.FileInfo] -or $item.Length -le 0) { throw "Expected nonempty file: $Path" }
    [ordered]@{ sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=$item.Length }
}
function Assert-Identity([string]$Path, [string]$Sha256, [long]$Bytes) {
    if ($Sha256 -cnotmatch '^[a-f0-9]{64}$' -or $Bytes -le 0) { throw 'Invalid expected file identity.' }
    $identity = Get-Identity $Path
    if ($identity.sha256 -cne $Sha256 -or $identity.bytes -ne $Bytes) { throw "File integrity mismatch: $Path" }
}
function Get-Scope {
    $text = & node (Join-Path $repo 'scripts/test-ai-release-approval.mjs') --print-scope
    if ($LASTEXITCODE -ne 0) { throw 'The production scope exporter failed.' }
    ($text -join "`n") | ConvertFrom-Json
}
function Get-SelectedModel([object]$Scope, [string]$SelectionId) {
    $matches = @($Scope.shippingModels | Where-Object { $_.id -ceq $SelectionId })
    if ($matches.Count -ne 1) { throw 'Explicit model ID must uniquely match the current shipping scope.' }
    if ($null -eq $Scope.modelProfiles.PSObject.Properties[$SelectionId]) { throw 'Selected model resource profile is missing.' }
    return $matches[0]
}
function Get-Canonical([object]$Value) { ConvertTo-Json -InputObject $Value -Depth 40 -Compress }
function Get-RuntimeInventory([string]$Directory) {
    Assert-PlainPath $Directory
    $names = @('LICENSE-llama.cpp', 'llama-completion-avx2.exe', 'llama-completion.exe', 'llama-tokenize.exe', 'plain-lyrics-worker-avx2.exe', 'plain-lyrics-worker-vulkan.exe', 'plain-lyrics-worker.exe', 'runtime-manifest.json')
    $actual = @(Get-ChildItem -LiteralPath $Directory -Force | Sort-Object Name)
    if (($actual.Name -join '|') -cne ($names -join '|')) { throw 'Runtime must contain exactly the eight shipping files, including LICENSE-llama.cpp.' }
    $manifestFile = Join-Path $Directory 'runtime-manifest.json'
    if ((Get-Item -LiteralPath $manifestFile).Length -gt 16384) { throw 'Runtime manifest exceeds the production bound.' }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.runtimeId -cne 'llama-cpp-v0.5.0-cpu-win-x64' -or $manifest.sourceCommit -cne '7fe450e19305b828c199d602c23a8337aaa1f03b') { throw 'Unexpected current runtime manifest.' }
    foreach ($entry in @(@{component=$manifest;name='llama-completion.exe'}, @{component=$manifest.avx2;name='llama-completion-avx2.exe'}, @{component=$manifest.tokenizer;name='llama-tokenize.exe'}, @{component=$manifest.resident.cpu;name='plain-lyrics-worker.exe'}, @{component=$manifest.resident.avx2;name='plain-lyrics-worker-avx2.exe'}, @{component=$manifest.resident.vulkan;name='plain-lyrics-worker-vulkan.exe'})) {
        if ($entry.component.executable -cne $entry.name) { throw 'Unexpected runtime component name.' }
        Assert-Identity (Join-Path $Directory $entry.name) $entry.component.sha256 $entry.component.bytes
    }
    if ($manifest.resident.protocol -ne 1 -or $manifest.resident.profile -cne 'hy-q8-plain-resident-v1' -or $manifest.resident.sourceSha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Missing or invalid production resident runtime profile.' }
    $license = (Get-Content -LiteralPath (Join-Path $Directory 'LICENSE-llama.cpp') -Raw).Replace('\', '/')
    foreach ($notice in @('===== LICENSE =====', '===== vendor/cpp-httplib/LICENSE =====', '===== licenses/LICENSE-jsonhpp =====')) {
        if (-not $license.Contains($notice)) { throw "Required runtime license notice missing: $notice" }
    }
    foreach ($name in $names) {
        $identity = Get-Identity (Join-Path $Directory $name)
        [ordered]@{ path=$name; sha256=$identity.sha256; bytes=$identity.bytes }
    }
}
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'Use PowerShell 7 on Windows x64.' }
Assert-PlainPath $OutputDirectory
$RuntimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$ModelPath = (Resolve-Path -LiteralPath $ModelPath).Path
$variantName = $Variant.ToLowerInvariant()
$cpu = [ordered]@{ avx2=[System.Runtime.Intrinsics.X86.Avx2]::IsSupported; fma=[System.Runtime.Intrinsics.X86.Fma]::IsSupported; x86=[System.Runtime.Intrinsics.X86.X86Base]::IsSupported; f16c=$false }
if ($cpu.x86) { $cpu.f16c=([System.Runtime.Intrinsics.X86.X86Base]::CpuId(1,0).Item3 -band (1 -shl 29)) -ne 0 }
Write-NewJson 'host.json' ([ordered]@{ executedAt=[DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); os=[Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture='x64'; runtimeVariant=$variantName; cpuQualification=$cpu; dotnet=[Environment]::Version.ToString(); githubRunId=$env:GITHUB_RUN_ID; githubRunAttempt=$env:GITHUB_RUN_ATTEMPT; githubSha=$env:GITHUB_SHA })
if ($Variant -ceq 'Avx2' -and (-not $cpu.avx2 -or -not $cpu.fma -or -not $cpu.x86 -or -not $cpu.f16c)) { throw 'Host lacks AVX2/FMA/F16C; no fallback variant is allowed.' }
$scope = Get-Scope
if ($scope.promptProfile -cne 'production-plain-hy' -or $scope.outputSchema -cne 'host-mapped-id-text-v1' -or $scope.promptVersion -cne 'official-plain-per-line-v1' -or $scope.backendId -cne 'hy-q8-plain-beta-v1' -or $scope.acceptanceVersion -cne 'unknown-copy-neutral-complete-song-v1') { throw 'Scope is not the frozen production plain Hy profile.' }
$model = Get-SelectedModel $scope $ModelId
$modelProfile = $scope.modelProfiles.$ModelId
Assert-Identity $ModelPath $model.sha256 $model.bytes
$runtimeBefore = @(Get-RuntimeInventory $RuntimeDirectory)
Write-NewJson 'runtime-inventory-before.json' $runtimeBefore
Write-NewJson 'scope.json' $scope
$scopePath = Join-Path $OutputDirectory 'scope.json'
$sourcePath = Join-Path $OutputDirectory 'source48.json'
if ($scope.fixture.path -cne 'scripts/ai-model-qa/inputs/source48.json') { throw 'Unexpected production fixture path.' }
$fixture = Join-Path $repo $scope.fixture.path
Assert-Identity $fixture $scope.fixture.sha256 (Get-Item -LiteralPath $fixture).Length
Copy-Item -LiteralPath $fixture -Destination $sourcePath
$runtimeSnapshot = Join-Path $OutputDirectory 'runtime-snapshot'
New-Item -ItemType Directory -Path $runtimeSnapshot | Out-Null
foreach ($file in $runtimeBefore) { Copy-Item -LiteralPath (Join-Path $RuntimeDirectory $file.path) -Destination (Join-Path $runtimeSnapshot $file.path) }
if ((Get-Canonical @(Get-RuntimeInventory $runtimeSnapshot)) -cne (Get-Canonical $runtimeBefore)) { throw 'Runtime snapshot differs.' }
foreach ($file in $scope.sources.files) {
    if ($file.path -match '\\|(^|/)\.\.?(/|$)' -or [IO.Path]::IsPathRooted($file.path)) { throw 'Invalid source snapshot path.' }
    $original = Join-Path $repo $file.path
    Assert-PlainPath $original
    $destination = Join-Path $OutputDirectory ('source-snapshot/' + $file.path)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $original -Destination $destination
}
$project = Join-Path $PSScriptRoot 'PlainHyProductionEvidence.csproj'
# Isolate the assembly embedding this capture's manifest; never reuse an older bin output.
$buildOutput = Join-Path $OutputDirectory 'harness-build'
& dotnet restore $project -p:RestoreLockedMode=true *> (Join-Path $OutputDirectory 'restore.log')
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed; see restore.log.' }
& dotnet build $project --no-restore --no-incremental -c Release -m:1 -p:UseSharedCompilation=false "-p:EvidenceRuntimeDirectory=$runtimeSnapshot" -o $buildOutput *> (Join-Path $OutputDirectory 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Production capture compilation failed; see build.log.' }
$dll = Join-Path $buildOutput 'PlainHyProductionEvidence.dll'
& dotnet $dll --contract-self-test *> (Join-Path $OutputDirectory 'contract-self-test.log')
if ($LASTEXITCODE -ne 0) { throw 'Production capture contract tests failed.' }
$argumentText = & dotnet $dll --print-native-arguments $ModelId
if ($LASTEXITCODE -ne 0) { throw 'Could not read production native arguments.' }
$nativeArguments = @((($argumentText -join "`n") | ConvertFrom-Json))
if ((Get-Canonical $nativeArguments) -cne (Get-Canonical @($modelProfile.nativeArguments))) { throw 'Built production native arguments differ from the invocation scope.' }
if ((Get-Canonical (Get-Scope)) -cne (Get-Canonical $scope)) { throw 'Production scope changed during compilation.' }
$manifestIdentity = Get-Identity (Join-Path $runtimeSnapshot 'runtime-manifest.json')
$manifest = Get-Content -LiteralPath (Join-Path $runtimeSnapshot 'runtime-manifest.json') -Raw | ConvertFrom-Json
$completion = if ($Variant -ceq 'Avx2') { $manifest.resident.avx2 } else { $manifest.resident.cpu }
if ($manifest.resident.protocol -ne $scope.runtime.resident.protocol -or $manifest.resident.profile -cne $scope.runtime.resident.profile -or $manifest.resident.sourceSha256 -cne $scope.runtime.resident.sourceSha256) { throw 'Built resident worker source/profile differs from the invocation scope.' }
$config = [ordered]@{
    schemaVersion=3; modelId=$model.id; modelSha256=$model.sha256; modelBytes=$model.bytes
    executableSha256=$completion.sha256; executableBytes=$completion.bytes; runtimeManifestSha256=$manifestIdentity.sha256; runtimeVariant=$variantName
    promptProfile=$scope.promptProfile; outputSchema=$scope.outputSchema; promptVersion=$scope.promptVersion; backendId=$scope.backendId
    acceptanceVersion=$scope.acceptanceVersion; samplerIdentity=$scope.samplerIdentity; nativeArguments=$nativeArguments; samplerArguments=@($scope.samplerArguments); executionLimits=$modelProfile.executionLimits; gpuEnabled=$false
    fixtureSha256=$scope.fixture.sha256; sourceFingerprintSha256=$scope.sources.sha256; loadOnly=$false
    captureMethod='PlainHyLyricsBackend+PlainHyLyricsCoordinator+PersistentPlainLyricsRunner.RunPlainAsync'
    progressContext=[ordered]@{playbackPositionSeconds=0;recordEphemeralUpdates=$true}
    repositoryRoot=$repo; runtimeDirectory=$runtimeSnapshot; modelPath=$ModelPath; executablePath=(Join-Path $runtimeSnapshot $completion.executable)
    outputDirectory=$OutputDirectory; sourcePath=$sourcePath; scopePath=$scopePath; logicalEvidenceRoot=$logicalRoot
}
Write-NewJson 'configuration.json' $config
$configPath = Join-Path $OutputDirectory 'configuration.json'
$configIdentity = Get-Identity $configPath
$scopeIdentity = Get-Identity $scopePath
# Freeze all execution inputs inside this capture. Source/model/runtime are rehashed afterwards too.
foreach ($path in @($configPath, $scopePath, $sourcePath) + @(Get-ChildItem -LiteralPath $runtimeSnapshot -File | ForEach-Object FullName) + @(Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'source-snapshot') -Recurse -File | ForEach-Object FullName)) { (Get-Item -LiteralPath $path).IsReadOnly = $true }
$executedAt = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
& dotnet $dll --capture $configPath *> (Join-Path $OutputDirectory 'capture.log')
$harnessExit = $LASTEXITCODE
# Always preserve post-execution observations, including unsuccessful/incomplete runs.
$scopeAfter = Get-Scope
Write-NewJson 'scope-after.json' $scopeAfter
$runtimeAfter = @(Get-RuntimeInventory $RuntimeDirectory)
Write-NewJson 'runtime-inventory-after.json' $runtimeAfter
$modelAfter = Get-Identity $ModelPath
Write-NewJson 'model-after.json' $modelAfter
Assert-Identity $configPath $configIdentity.sha256 $configIdentity.bytes
Assert-Identity $scopePath $scopeIdentity.sha256 $scopeIdentity.bytes
if ((Get-Canonical $scopeAfter) -cne (Get-Canonical $scope)) { throw 'Production source or fixture changed during execution.' }
if ((Get-Canonical $runtimeAfter) -cne (Get-Canonical $runtimeBefore)) { throw 'Original runtime changed during execution.' }
Assert-Identity $ModelPath $model.sha256 $model.bytes
if ($harnessExit -ne 0) { throw "Native capture failed or was incomplete (exit $harnessExit); retain all output, no automatic rerun." }
$technical = Get-Content -LiteralPath (Join-Path $OutputDirectory 'technical-results.json') -Raw | ConvertFrom-Json
if ($technical.status -cne 'complete') { throw 'Native technical checks did not complete.' }
$outputs = foreach ($target in @('en','zh-Hans')) {
    $name = $target + '.runner-output.json'
    $identity = Get-Identity (Join-Path $OutputDirectory $name)
    [ordered]@{kind='runner-output';targetLanguage=$target;path="$logicalRoot/$name";sha256=$identity.sha256}
}
$cacheOutputs = foreach ($target in @('en','zh-Hans')) {
    $name = $target + '.cache-output.json'
    $identity = Get-Identity (Join-Path $OutputDirectory $name)
    [ordered]@{targetLanguage=$target;path="$logicalRoot/$name";sha256=$identity.sha256}
}
$checks = [ordered]@{}
foreach ($name in @('coldTargets','cacheTargets','cacheAdditionalInferenceCalls','cancellationObserved','cleanupConfirmed','sourceIdentityUnchanged','modelIdentityUnchanged','runtimeIdentityUnchanged')) { $checks[$name]=$technical.$name }
$gpuProbe = Get-Content -LiteralPath (Join-Path $OutputDirectory 'gpu-default-probe.json') -Raw | ConvertFrom-Json
if ($gpuProbe.gpuEnabled -ne $true -or $gpuProbe.complete -ne $true -or $gpuProbe.cleanupConfirmed -ne $true -or $gpuProbe.deviceVendor -cne 'unverified') { throw 'The separate default-setting GPU probe did not complete and clean up.' }
$gpuProbeIdentity = Get-Identity (Join-Path $OutputDirectory 'gpu-default-probe.json')
$envelope = [ordered]@{
    schemaVersion=3;kind='native-output';platform='windows-x64';executedAt=$executedAt
    promptProfile=$scope.promptProfile;outputSchema=$scope.outputSchema;promptVersion=$scope.promptVersion;backendId=$scope.backendId;acceptanceVersion=$scope.acceptanceVersion
    samplerIdentity=$scope.samplerIdentity;executionLimits=$modelProfile.executionLimits;captureMethod=$config.captureMethod
    fixtureSha256=$scope.fixture.sha256;sourceFingerprintSha256=$scope.sources.sha256
    model=[ordered]@{id=$model.id;sha256=$model.sha256;bytes=$model.bytes}
    runtime=[ordered]@{manifestSha256=$manifestIdentity.sha256;variant=$variantName;mode='cpu';protocol=$manifest.resident.protocol;profile=$manifest.resident.profile;residentSourceSha256=$manifest.resident.sourceSha256;completion=[ordered]@{sha256=$completion.sha256;bytes=$completion.bytes}}
    configuration=[ordered]@{path="$logicalRoot/configuration.json";sha256=$configIdentity.sha256}
    outputs=@($outputs);cacheOutputs=@($cacheOutputs);technicalChecks=$checks
    gpuDefaultProbe=[ordered]@{path="$logicalRoot/gpu-default-probe.json";sha256=$gpuProbeIdentity.sha256}
}
Write-NewJson 'native-output.json' $envelope
$envelopeIdentity = Get-Identity (Join-Path $OutputDirectory 'native-output.json')
Write-NewJson 'evidence-reference.json' ([ordered]@{kind='native-output';fixtureSha256=$scope.fixture.sha256;path="$logicalRoot/native-output.json";sha256=$envelopeIdentity.sha256})
Write-Host "Production capture complete: $OutputDirectory. Runner-returned output requires independent review; no semantic verdict or release approval was written."
