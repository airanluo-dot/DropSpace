[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($SourceRoot)
$controllerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($root -ceq $controllerRoot) { throw 'Application source and orchestration checkouts must be separate.' }
$diagnostics = Join-Path $root 'artifacts/isolated-7b-diagnostics'
$work = Join-Path $root 'artifacts/isolated-7b-work'
if (Test-Path $diagnostics) { throw 'Diagnostics destination must be fresh.' }
New-Item $diagnostics -ItemType Directory | Out-Null

function Write-Json([string]$Name, [object]$Value) {
    $path = Join-Path $diagnostics $Name
    if (Test-Path $path) { throw 'Refusing to replace evidence.' }
    $Value | ConvertTo-Json -Depth 50 | Set-Content $path -Encoding utf8NoBOM
}
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

try {
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'Windows x64 is required.' }
    if ($env:GITHUB_REPOSITORY -cne 'airanluo-dot/DropSpace') { throw 'Unexpected repository.' }
    if ($env:EVIDENCE_MODE -cnotin @('preflight', 'capture')) { throw 'Unexpected mode.' }
    $expected = $env:EVIDENCE_EXPECTED_COMMIT
    if ($expected -cnotmatch '^[a-f0-9]{40}$') { throw 'Expected application source must be an exact lowercase SHA.' }
    $controllerCommit = (& git -C $controllerRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $controllerCommit -cne $env:GITHUB_SHA) { throw 'Orchestration checkout does not match its own event SHA.' }
    if ($env:GITHUB_EVENT_NAME -ceq 'push') {
        if ($env:GITHUB_REF -cne 'refs/heads/qa/windows-7b-preflight-20261003' -or $env:EVIDENCE_TRIGGER_IS_NEW_BRANCH -cne 'true' -or $env:EVIDENCE_MODE -cne 'preflight' -or $expected -cne 'fedc4eeb48fbeaf4a3424edb191eb2eaacf71dc8' -or $env:GITHUB_RUN_ATTEMPT -cne '1') { throw 'Only the single bounded new-branch preflight is permitted for push.' }
    } elseif ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch') { throw 'Unexpected trigger.' }
    $actual = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -cne $expected) { throw 'Checkout source mismatch.' }
    $headers = @{ Authorization="Bearer $env:GH_TOKEN"; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
    $repo = Invoke-RestMethod -Uri 'https://api.github.com/repos/airanluo-dot/DropSpace' -Headers $headers
    if ($repo.private -ne $false) { throw 'Stop: free public standard-runner assumption is not satisfied.' }
    $scopeText = & node (Join-Path $root 'scripts/test-ai-release-approval.mjs') --print-scope
    if ($LASTEXITCODE -ne 0) { throw 'Current production scope could not be read.' }
    $scope = ($scopeText -join "`n") | ConvertFrom-Json
    $modelId = 'hy-mt2-7b-q8-plain-beta'
    $model = @($scope.shippingModels | Where-Object { $_.id -ceq $modelId })
    if ($model.Count -ne 1 -or $model[0].bytes -ne 7981928896 -or $model[0].sha256 -cne '58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0') { throw 'Frozen 7B catalog identity changed; review required.' }
    $model = $model[0]
    $profile = $scope.modelProfiles.$modelId
    if ($profile.executionLimits.memoryMiB -ne 12288 -or $profile.cpuMemoryAdmission.minimumAvailableBytes -ne 13GB) { throw 'Production 12GiB child cap / 13GiB admission mismatch.' }
    Write-Json 'source-scope.json' $scope
    Write-Json 'request.json' ([ordered]@{ expectedCommit=$expected; sourceCommit=$actual; orchestratorCommit=$controllerCommit; orchestratorWorkflowRef=$env:GITHUB_WORKFLOW_REF; orchestratorWorkflowSha=$env:GITHUB_WORKFLOW_SHA; mode=$env:EVIDENCE_MODE; runId=$env:GITHUB_RUN_ID; runAttempt=$env:GITHUB_RUN_ATTEMPT; runnerLabel='windows-2025'; model=$model; semanticStatus='not-evaluated'; publicationRequested=$false })

    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Isolated7bHostMemory {
  [StructLayout(LayoutKind.Sequential)]
  public struct Status {
    public uint Length, MemoryLoad;
    public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
    public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
  }
  [DllImport("kernel32.dll", SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool GlobalMemoryStatusEx(ref Status status);
  public static Status Read() {
    var status = new Status { Length=(uint)Marshal.SizeOf<Status>() };
    if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx failed.");
    if (status.AvailablePhysical > status.TotalPhysical || status.AvailablePageFile > status.TotalPageFile)
      throw new InvalidOperationException("Invalid memory snapshot.");
    return status;
  }
}
'@
    function Assert-Host([string]$Stage, [long]$RequiredDiskBytes) {
        $memory = [Isolated7bHostMemory]::Read()
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))
        $snapshot = [ordered]@{
            observedAt=[DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
            stage=$Stage; totalPhysicalBytes=$memory.TotalPhysical
            availablePhysicalBytes=$memory.AvailablePhysical; availableCommitBytes=$memory.AvailablePageFile
            requiredAvailableBytes=13GB; availableDiskBytes=$drive.AvailableFreeSpace; requiredDiskBytes=$RequiredDiskBytes
            passed=($memory.AvailablePhysical -ge 13GB -and $memory.AvailablePageFile -ge 13GB -and $drive.AvailableFreeSpace -ge $RequiredDiskBytes)
        }
        Write-Json "host-$Stage.json" $snapshot
        if (-not $snapshot.passed) { throw "Host preflight failed at $Stage; no threshold changes or automatic retries are permitted." }
    }

    # Coarse no-download check. Capture performs a tighter inventory-based check later.
    Assert-Host 'initial' ([long]$model.bytes + 2GB)
    if ($env:EVIDENCE_MODE -ceq 'preflight') {
        Write-Json 'preflight-result.json' ([ordered]@{ status='preflight-passed'; modelDownloaded=$false; runtimeDownloaded=$false; inferenceStarted=$false; runtimeProvenanceVerified=$false; semanticStatus='not-evaluated'; limitation='Snapshot only; capture performs fresh resource checks.' })
        return
    }

    foreach ($name in @('EVIDENCE_RUNTIME_RUN_ID','EVIDENCE_RUNTIME_RUN_ATTEMPT','EVIDENCE_RUNTIME_ARTIFACT_ID')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value -cnotmatch '^[1-9][0-9]{0,14}$') { throw "Missing or invalid $name." }
    }
    $archiveHash = $env:EVIDENCE_RUNTIME_ARCHIVE_SHA256
    if ($archiveHash -cnotmatch '^[a-f0-9]{64}$') { throw 'Exact runtime archive digest is required.' }
    $runId = [long]$env:EVIDENCE_RUNTIME_RUN_ID
    $attempt = [int]$env:EVIDENCE_RUNTIME_RUN_ATTEMPT
    $artifactId = [long]$env:EVIDENCE_RUNTIME_ARTIFACT_ID
    $api = 'https://api.github.com/repos/airanluo-dot/DropSpace/actions'
    $artifact = Invoke-RestMethod -Uri "$api/artifacts/$artifactId" -Headers $headers
    $producer = Invoke-RestMethod -Uri "$api/runs/$runId/attempts/$attempt" -Headers $headers
    Write-Json 'runtime-artifact.json' $artifact
    Write-Json 'runtime-producer-run.json' $producer
    if ($producer.id -ne $runId -or $producer.run_attempt -ne $attempt -or $producer.head_sha -cne $expected -or $producer.path -cne '.github/workflows/release.yml' -or $producer.status -cne 'completed' -or $producer.conclusion -cne 'success') { throw 'Exact successful Release producer is required.' }
    if ($artifact.id -ne $artifactId -or $artifact.expired -ne $false -or $artifact.name -cne "ai-candidate-runtime-$runId-$attempt" -or $artifact.workflow_run.id -ne $runId -or $artifact.workflow_run.head_sha -cne $expected -or $artifact.digest -cne "sha256:$archiveHash" -or $artifact.size_in_bytes -le 0 -or $artifact.size_in_bytes -gt 2GB) { throw 'Runtime artifact identity/expiry/digest/size mismatch.' }
    if ([DateTimeOffset]::Parse($artifact.expires_at) -le [DateTimeOffset]::UtcNow) { throw 'Runtime artifact is expired.' }
    if (Test-Path $work) { throw 'Work destination must be fresh.' }
    New-Item $work -ItemType Directory | Out-Null
    $archive = Join-Path $work 'runtime.zip'
    Invoke-WebRequest -Uri "$api/artifacts/$artifactId/zip" -Headers $headers -OutFile $archive -TimeoutSec 300
    if ((Get-Item $archive).Length -ne $artifact.size_in_bytes -or (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $archiveHash) { throw 'Downloaded runtime archive size or digest mismatch.' }
    $runtime = Join-Path $work 'runtime'
    New-Item $runtime -ItemType Directory | Out-Null
    $allowed = @('LICENSE-llama.cpp','llama-completion-avx2.exe','llama-completion.exe','llama-tokenize.exe','plain-lyrics-worker-avx2.exe','plain-lyrics-worker-vulkan.exe','plain-lyrics-worker.exe','runtime-manifest.json')
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entries = @($zip.Entries)
        if ($entries.Count -ne $allowed.Count -or ((@($entries.FullName | Sort-Object) -join '|') -cne (@($allowed | Sort-Object) -join '|'))) { throw 'Runtime ZIP must contain exactly the eight fixed root-level files.' }
        $runtimeBytes = [long]0
        foreach ($entry in $entries) {
            if ($entry.Length -le 0) { throw 'Empty runtime ZIP entry.' }
            $runtimeBytes += $entry.Length
            if ($runtimeBytes -gt 2GB) { throw 'Runtime payload exceeds the existing 2GiB inventory limit.' }
        }
        foreach ($entry in $entries) {
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Open((Join-Path $runtime $entry.FullName), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
    Invoke-Checked 'node' @((Join-Path $PSScriptRoot 'verify-runtime.mjs'), $root, $runtime, (Join-Path $diagnostics 'runtime-artifact.json'), (Join-Path $diagnostics 'runtime-producer-run.json'), $expected, "$runId", "$attempt", "$artifactId", $archiveHash, (Join-Path $diagnostics 'runtime-artifact-contract.json'))

    # Four future copies cover two runtime snapshots plus two embedded harness outputs.
    Assert-Host 'before-model-download' ([long]$model.bytes + 4 * $runtimeBytes + 2GB)
    $modelPath = Join-Path $work 'HY-MT2-7B-Q8_0.gguf'
    $modelUrl = 'https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/ab8472660ac61fac25f1af43fac2599d52a8a775/HY-MT2-7B-Q8_0.gguf'
    Invoke-WebRequest -Uri $modelUrl -OutFile $modelPath -TimeoutSec 1200
    if ((Get-Item $modelPath).Length -ne $model.bytes -or (Get-FileHash $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $model.sha256) { throw 'Pinned 7B model size/hash verification failed.' }
    Write-Json 'model-identity.json' ([ordered]@{ id=$model.id; bytes=$model.bytes; sha256=$model.sha256; url=$modelUrl })
    Assert-Host 'after-model-download' (4 * $runtimeBytes + 2GB)
    $captureRoot = Join-Path $work 'captures'
    New-Item $captureRoot -ItemType Directory | Out-Null
    $index = 0
    foreach ($variant in @('Baseline','Avx2')) {
        Assert-Host "before-$($variant.ToLowerInvariant())" ((4 - 2 * $index) * $runtimeBytes + 2GB)
        $name = "production-plain-hy-7b-$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)-$($variant.ToLowerInvariant())"
        $capture = Join-Path $captureRoot $name
        try {
            & (Join-Path $root 'scripts/plain-hy-production-evidence/Run-WindowsProductionEvidence.ps1') -RuntimeDirectory $runtime -ModelPath $modelPath -ModelId $modelId -Variant $variant -OutputDirectory $capture
            if (-not $? -or $LASTEXITCODE -ne 0) { throw "Production $variant capture failed." }
            if (-not (Test-Path (Join-Path $capture 'native-output.json'))) { throw 'Production capture returned without its native envelope.' }
        } finally {
            # Capture writes evidence references using this original directory name.
            # Preserve those names, but never upload models, binaries or source snapshots.
            if (Test-Path $capture) {
                $saved = Join-Path $diagnostics $name
                New-Item $saved -ItemType Directory | Out-Null
                foreach ($file in Get-ChildItem $capture -File) {
                    if ($file.Extension -cin @('.json','.log','.txt')) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $saved $file.Name) }
                }
            }
        }
        $index++
    }
    Write-Json 'capture-result.json' ([ordered]@{ status='technical-capture-completed'; modelId=$modelId; variants=@('baseline','avx2'); expectedCommit=$expected; semanticStatus='not-evaluated'; approvalWritten=$false; publicationRequested=$false })
} catch {
    Write-Json 'failure.json' ([ordered]@{ status='failed-or-incomplete'; error=$_.ToString(); details=$_.ScriptStackTrace; semanticStatus='not-evaluated'; approvalWritten=$false; publicationRequested=$false })
    throw
}
