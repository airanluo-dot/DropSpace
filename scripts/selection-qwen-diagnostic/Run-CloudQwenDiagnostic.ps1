# Explicit diagnostic download consent: 2026-10-05T01:11:06Z / Sentinel 208dd5c270f48191a5f2ac46576d7c28.
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
$modelDirectory=Join-Path $env:RUNNER_TEMP 'dropspace-qwen06-diagnostic'
New-Item $modelDirectory -ItemType Directory -Force | Out-Null
$model=Join-Path $modelDirectory 'Qwen3-0.6B-Q8_0.gguf'
$partial=$model+'.partial'
$url='https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/23749fefcc72300e3a2ad315e1317431b06b590a/Qwen3-0.6B-Q8_0.gguf'
$clock=[Diagnostics.Stopwatch]::StartNew()
# No cookies, credentials, mirrors, rotating IPs, or repeated model probes.
Invoke-WebRequest -Uri $url -OutFile $partial
if((Get-Item -LiteralPath $partial).Length -ne 639446688 -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine '9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031') { throw 'Immutable official model verification failed.' }
Move-Item -LiteralPath $partial -Destination $model
[ordered]@{url=$url;bytes=639446688;sha256='9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031';verified=$true;downloadAndVerificationSeconds=$clock.Elapsed.TotalSeconds;authorizationUtc='2026-10-05T01:11:06Z';authorizationSentinel='208dd5c270f48191a5f2ac46576d7c28'} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'download.json') -Encoding utf8
Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,FreePhysicalMemory,TotalVisibleMemorySize | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'host.json') -Encoding utf8
Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'cpu.json') -Encoding utf8
& dotnet run --project (Join-Path $PSScriptRoot 'SelectionQwenDiagnostic.csproj') -c Release "-p:EvidenceRuntimeDirectory=$InputsDirectory/runtime" -- $InputsDirectory $model $OutputDirectory
if($LASTEXITCODE -ne 0) { throw 'Qwen diagnostic execution failed; retained evidence is not model approval.' }
