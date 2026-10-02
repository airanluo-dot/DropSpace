# Release-only consumption of the exact artifact named by a validated semantic
# review. There is no latest-artifact, source rebuild, or alternate-download fallback.
[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/ai-runtime/win-x64')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory)) }
if (Test-Path -LiteralPath $output) { throw 'Reviewed runtime destination must be new.' }
if (-not $env:GH_TOKEN) { throw 'The existing read-only Actions token is required.' }
$work = Join-Path $root ('artifacts/runtime-retrieval-' + [Guid]::NewGuid().ToString('N'))
New-Item $work -ItemType Directory | Out-Null
$contractPath = Join-Path $work 'contract.json'
& node (Join-Path $PSScriptRoot 'test-ai-release-approval.mjs') --export-runtime-contract $contractPath
if ($LASTEXITCODE -ne 0) { throw 'A current semantic review of an exact runtime artifact is required.' }
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ($env:GITHUB_REPOSITORY -cne $contract.repository) { throw 'Runtime artifact repository mismatch.' }
$headers = @{ Authorization="Bearer $env:GH_TOKEN"; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
$api = "https://api.github.com/repos/$($contract.repository)/actions"
$artifact = Invoke-RestMethod -Uri "$api/artifacts/$($contract.artifactId)" -Headers $headers
$run = Invoke-RestMethod -Uri "$api/runs/$($contract.runId)/attempts/$($contract.runAttempt)" -Headers $headers
$artifactPath = Join-Path $work 'artifact.json'
$runPath = Join-Path $work 'run.json'
$artifact | ConvertTo-Json -Depth 30 | Set-Content $artifactPath -Encoding utf8
$run | ConvertTo-Json -Depth 30 | Set-Content $runPath -Encoding utf8
& node (Join-Path $PSScriptRoot 'ai-runtime-publication.mjs') verify-artifact $contractPath $artifactPath $runPath
if ($LASTEXITCODE -ne 0) { throw 'Reviewed artifact provenance verification failed.' }
if ($artifact.size_in_bytes -le 0 -or $artifact.size_in_bytes -gt 2GB) { throw 'Runtime archive size is outside the supported bound.' }
$archive = Join-Path $work 'runtime.zip'
Invoke-WebRequest -Uri "$api/artifacts/$($contract.artifactId)/zip" -Headers $headers -OutFile $archive
if ((Get-Item $archive).Length -ne $artifact.size_in_bytes -or
    (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $contract.archiveSha256) {
    throw 'Reviewed runtime archive size/SHA256 mismatch.'
}

& (Join-Path $PSScriptRoot 'Expand-ReviewedAiRuntime.ps1') -ArchivePath $archive -ContractPath $contractPath -OutputDirectory $output
& node (Join-Path $PSScriptRoot 'test-ai-release-approval.mjs') --runtime-manifest (Join-Path $output 'runtime-manifest.json')
if ($LASTEXITCODE -ne 0) { throw 'Extracted runtime differs from the reviewed bytes.' }
Write-Host 'Exact reviewed runtime artifact retrieved and verified. No rebuild was performed.'
