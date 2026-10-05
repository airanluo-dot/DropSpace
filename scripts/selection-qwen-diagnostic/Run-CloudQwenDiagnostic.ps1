# Each immutable profile carries its own explicit diagnostic download authorization.
# Cloud only; no production catalog/default/distribution change, no native rebuild.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$InputsDirectory,[Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true') { throw 'This authorized diagnostic runs only on cloud Windows Actions.' }
if(Test-Path -LiteralPath $OutputDirectory) { throw 'Prior evidence must not be overwritten.' }
New-Item $OutputDirectory -ItemType Directory | Out-Null
trap {
  [ordered]@{diagnosticOnly=$true;error=$_.ToString();details=$_.ScriptStackTrace} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'setup-failure.json') -Encoding utf8
  Write-Error -ErrorRecord $_ -ErrorAction Continue
  exit 1
}
$profileId=if($env:DIAGNOSTIC_VARIANT -in @('qwen4-evidence','qwen4-template-validation','qwen4-deterministic')) {'qwen3-4b-instruct-2507-q8'} else {'qwen3-06-q8'}
$profiles=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'diagnostic-profiles.json') -Raw | ConvertFrom-Json
$profile=@($profiles | Where-Object Id -CEQ $profileId)
if($profile.Count -ne 1) {throw 'Unknown pinned diagnostic model profile.'}
$profile=$profile[0]
$modelDirectory=Join-Path $env:RUNNER_TEMP ('dropspace-diagnostic-'+$profile.Id)
New-Item $modelDirectory -ItemType Directory -Force | Out-Null
$model=Join-Path $modelDirectory $profile.File
$partial=$model+'.partial'
$clock=[Diagnostics.Stopwatch]::StartNew()
Write-Host "Locating authorized immutable $($profile.Id), $($profile.Bytes) bytes; verification pending."
$reused=Test-Path -LiteralPath $model
# No cookies, credentials, mirrors, IP rotation, or production model registration.
if (-not (Test-Path -LiteralPath $model)) {
  Invoke-WebRequest -Uri $profile.Url -OutFile $partial
  if((Get-Item -LiteralPath $partial).Length -ne $profile.Bytes -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine $profile.Sha256) { throw 'Immutable model verification failed.' }
  Move-Item -LiteralPath $partial -Destination $model
}
if((Get-Item -LiteralPath $model).Length -ne $profile.Bytes -or (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -ine $profile.Sha256) { throw 'Immutable model verification failed.' }
[ordered]@{profile=$profile;verified=$true;reusedPresentFile=$reused;downloadAndVerificationSeconds=$clock.Elapsed.TotalSeconds} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'download.json') -Encoding utf8
Write-Host "Verified $($profile.Id): exact bytes and SHA256 matched."
Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,FreePhysicalMemory,TotalVisibleMemorySize | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'host.json') -Encoding utf8
Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'cpu.json') -Encoding utf8
& dotnet run --project (Join-Path $PSScriptRoot 'SelectionQwenDiagnostic.csproj') -c Release "-p:EvidenceRuntimeDirectory=$InputsDirectory/runtime" -- $InputsDirectory $model $OutputDirectory
if($LASTEXITCODE -ne 0) { throw 'Qwen diagnostic execution failed; retained evidence is not model approval.' }
