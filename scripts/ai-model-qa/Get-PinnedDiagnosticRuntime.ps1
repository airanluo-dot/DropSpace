# Read-only retrieval of one reviewed same-repository runtime artifact. No arbitrary executable fallback.
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/ai-candidate-runtime',
    [string]$EvidenceDirectory = 'artifacts/ai-model-diagnostics/runtime-provenance'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = 'airanluo-dot/DropSpace'
$runId = 36965634264L
$attempt = 1
$artifactId = 11210435635L
$artifactName = 'ai-candidate-runtime-36965634264-1'
$archiveSha256 = '442f9d39cc7ea758c198874ae32909e50e3cfba58fa6b0ddf747fee02674879e'
$manifestSha256 = '406dfe47bd7c938936a4f57dd0ac4ecba910d8c5fd893aa72877ca172e26b212'
$buildSha = '61596fcb6e757de2e6496613be3791a38142e04d'
$runtimeSource = '7fe450e19305b828c199d602c23a8337aaa1f03b'
New-Item $EvidenceDirectory -ItemType Directory -Force | Out-Null
trap {
    [ordered]@{ status='FAILED'; stage='Pinned runtime retrieval or verification'; errorType=$_.Exception.GetType().FullName; message='Fixed artifact unavailable or verification failed; no fallback was attempted.' } |
        ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory 'failure.json') -Encoding utf8
    throw $_
}
if ($env:GITHUB_REPOSITORY -cne $repository) { throw 'Pinned runtime belongs to a different repository.' }
if (-not $env:GH_TOKEN) { throw 'Read-only GitHub token is required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Runtime destination must be new.' }
$headers = @{ Authorization="Bearer $env:GH_TOKEN"; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
$api = "https://api.github.com/repos/$repository/actions"
$artifact = Invoke-RestMethod -Uri "$api/artifacts/$artifactId" -Headers $headers
$run = Invoke-RestMethod -Uri "$api/runs/$runId/attempts/$attempt" -Headers $headers
if ($artifact.id -ne $artifactId -or $artifact.name -cne $artifactName -or $artifact.expired -or
    $artifact.digest -cne "sha256:$archiveSha256" -or $artifact.workflow_run.id -ne $runId -or
    $artifact.workflow_run.head_sha -cne $buildSha) { throw 'Fixed artifact identity/digest mismatch or artifact expired.' }
if ($run.id -ne $runId -or $run.run_attempt -ne $attempt -or $run.head_sha -cne $buildSha -or
    $run.repository.full_name -cne $repository -or $run.status -cne 'completed' -or $run.conclusion -cne 'success') {
    throw 'Pinned runtime build run identity or successful completion could not be verified.'
}
$archive = Join-Path (Split-Path ([IO.Path]::GetFullPath($OutputDirectory)) -Parent) 'pinned-ai-runtime.zip'
New-Item (Split-Path $archive -Parent) -ItemType Directory -Force | Out-Null
Invoke-WebRequest -Uri "$api/artifacts/$artifactId/zip" -Headers $headers -OutFile $archive
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ine $archiveSha256) { throw 'Runtime archive SHA256 mismatch.' }
$allowed = @('runtime-manifest.json','llama-completion.exe','llama-completion-avx2.exe','llama-tokenize.exe','LICENSE-llama.cpp')
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -cnotin $allowed -or -not $seen.Add($entry.FullName)) { throw 'Unexpected or duplicate runtime archive entry.' }
    }
    foreach ($required in $allowed[0..3]) { if (-not $seen.Contains($required)) { throw 'Missing pinned runtime component.' } }
} finally { $zip.Dispose() }
Expand-Archive -LiteralPath $archive -DestinationPath $OutputDirectory
$manifestPath = Join-Path $OutputDirectory 'runtime-manifest.json'
if ((Get-FileHash $manifestPath -Algorithm SHA256).Hash -ine $manifestSha256) { throw 'Runtime manifest SHA256 mismatch.' }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.runtimeId -cne 'llama-cpp-v0.5.0-cpu-win-x64' -or $manifest.sourceCommit -cne $runtimeSource) { throw 'Runtime source identity mismatch.' }
foreach ($component in @($manifest,$manifest.avx2,$manifest.tokenizer)) {
    if ($component.executable -cnotin $allowed[1..3]) { throw 'Unexpected manifest executable.' }
    $path = Join-Path $OutputDirectory $component.executable
    if ((Get-Item $path).Length -ne $component.bytes -or (Get-FileHash $path -Algorithm SHA256).Hash -ine $component.sha256) { throw 'Native component size/hash mismatch.' }
}
[ordered]@{
    status='VERIFIED'; repository=$repository; runtimeBuildRunId=$runId; runtimeBuildAttempt=$attempt
    runtimeBuildSourceSha=$buildSha; experimentSourceSha=$env:GITHUB_SHA; llamaSourceCommit=$runtimeSource
    artifactId=$artifactId; artifactName=$artifactName; archiveSha256=$archiveSha256
    runtimeManifestSha256=$manifestSha256; runtimeBuildConclusion=$run.conclusion
    licenseIncluded=(Test-Path (Join-Path $OutputDirectory 'LICENSE-llama.cpp'))
    note='Legacy pinned artifact predates notice inclusion. Future Release runtime uploads include LICENSE-llama.cpp. This is QA reuse, not redistribution approval.'
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $EvidenceDirectory 'verified-runtime.json') -Encoding utf8
Copy-Item $manifestPath (Join-Path $EvidenceDirectory 'runtime-manifest.json')
Write-Host "Verified fixed runtime artifact $artifactId from build $buildSha; experiment source is $env:GITHUB_SHA"
