param([string]$OutputDirectory = "$env:RUNNER_TEMP/m2m-evidence", [string]$ModelDirectory = "$env:RUNNER_TEMP/m2m-models")
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'Windows x64 required' }
$qa = $PSScriptRoot
$dependencyQa = Join-Path $qa '../marian-model-qa'
$repo = (Resolve-Path (Join-Path $qa '../..')).Path
New-Item -ItemType Directory -Force $OutputDirectory, $ModelDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$ModelDirectory = (Resolve-Path $ModelDirectory).Path
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$env:HF_HUB_OFFLINE = '1'
$env:TRANSFORMERS_OFFLINE = '1'
$env:OMP_NUM_THREADS = '4'
$env:MKL_NUM_THREADS = '4'
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 $Path).Hash.ToLowerInvariant() }
$pythonBase = (Get-Command python).Source
$version = & $pythonBase -c 'import platform; print(platform.python_version())'
if ($LASTEXITCODE -ne 0 -or $version -ne '3.11.9') { throw 'Requires fixed Python 3.11.9' }
$venv = Join-Path $ModelDirectory 'venv'
& $pythonBase -m venv $venv
if ($LASTEXITCODE -ne 0) { throw 'venv failed' }
$python = Join-Path $venv 'Scripts/python.exe'
# Installation is build preparation, never included in inference resource claims.
& $python -m pip install --disable-pip-version-check --only-binary=:all: --require-hashes -r (Join-Path $dependencyQa 'requirements-win-py311.lock') 2>&1 | Tee-Object (Join-Path $OutputDirectory 'dependency-install.log')
if ($LASTEXITCODE -ne 0) { throw 'Locked dependency installation failed' }
& $python -m pip check 2>&1 | Tee-Object (Join-Path $OutputDirectory 'dependency-check.log')
if ($LASTEXITCODE -ne 0) { throw 'Dependency compatibility failed' }
& $python -m pip freeze --all | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'installed-packages.txt')
if ($LASTEXITCODE -ne 0) { throw 'Cannot record installed packages' }
Copy-Item (Join-Path $dependencyQa 'dependencies.json'), (Join-Path $dependencyQa 'requirements-win-py311.lock'), (Join-Path $qa 'protocol.json'), (Join-Path $qa 'models.json') $OutputDirectory
$sourceComponents = Get-ChildItem $qa -File | ForEach-Object { @{ path = $_.Name; bytes = $_.Length; sha256 = Hash $_.FullName } }
@{ files = @($sourceComponents) } | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'qa-source-manifest.json')
& $python -X utf8 -m unittest discover -s $qa -p 'test_*.py' -v 2>&1 | Tee-Object (Join-Path $OutputDirectory 'protocol-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed' }
$runtimeComponents = Get-ChildItem $venv -Recurse -File | Where-Object { $_.Extension -in '.exe','.dll','.pyd' } | ForEach-Object { @{ path = [IO.Path]::GetRelativePath($venv, $_.FullName); bytes = $_.Length; sha256 = Hash $_.FullName } }
$baseRoot = Split-Path $pythonBase
$baseFiles = @((Get-ChildItem $baseRoot -File | Where-Object { $_.Extension -in '.exe','.dll' }))
if (Test-Path (Join-Path $baseRoot 'DLLs')) { $baseFiles += @(Get-ChildItem (Join-Path $baseRoot 'DLLs') -File | Where-Object { $_.Extension -in '.dll','.pyd' }) }
$baseComponents = $baseFiles | ForEach-Object { @{ path = [IO.Path]::GetRelativePath($baseRoot, $_.FullName); bytes = $_.Length; sha256 = Hash $_.FullName } }
@{ python = $version; pythonBaseSha256 = Hash $pythonBase; pythonBaseComponents = @($baseComponents); components = @($runtimeComponents); inferenceProtocol = 'offline-private-process-ct2-int8-beam4'; sourceSha = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT } | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'runtime-manifest.json')
$freeRam = [int64](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1024
$volume = Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($ModelDirectory).Substring(0,1))
@{ availableRamBytes = $freeRam; availableDiskBytes = $volume.SizeRemaining; minimumRamBytes = 10GB; minimumDiskBytes = 18GB; phase = 'build-conversion-only'; inferenceCapBytes = 3GB } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'conversion-preflight.json')
if ($freeRam -lt 10GB -or $volume.SizeRemaining -lt 18GB) { throw 'M2M conversion requires 10 GiB available RAM and 18 GiB disk; no inference cap change' }
foreach ($phase in @('sanitize','convert')) {
    $buildArgs = @('-I','-S','-X','utf8',(Join-Path $qa 'build_models.py'),'--site-packages',(Join-Path $venv 'Lib/site-packages'),'--phase',$phase,'--manifest',(Join-Path $qa 'models.json'),'--root',$ModelDirectory,'--evidence',$OutputDirectory)
    $quotedArgs = $buildArgs | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }
    $child = Start-Process -FilePath $pythonBase -ArgumentList $quotedArgs -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $OutputDirectory "$phase.stdout.log") -RedirectStandardError (Join-Path $OutputDirectory "$phase.stderr.log")
    $clock = [Diagnostics.Stopwatch]::StartNew(); $peakRss = 0L; $peakPrivate = 0L; $reason = $null
    try {
        while (-not $child.HasExited) {
            $child.Refresh()
            $peakRss = [Math]::Max($peakRss, $child.WorkingSet64)
            $peakPrivate = [Math]::Max($peakPrivate, $child.PrivateMemorySize64)
            if ($peakRss -gt 10GB -or $peakPrivate -gt 10GB -or $clock.Elapsed.TotalSeconds -gt 900) {
                $reason = 'Build resource or 900-second budget exceeded'; $child.Kill($true); break
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $child.WaitForExit(10000)) { throw 'Build child exit unconfirmed; no inference attempted' }
        @{ phase = $phase; seconds = $clock.Elapsed.TotalSeconds; exitCode = $child.ExitCode; reason = $reason; peakRssBytes = $peakRss; peakPrivateBytes = $peakPrivate; inferenceMeasurement = $false; cleanupConfirmed = $child.HasExited } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $OutputDirectory "$phase.native.json")
        if ($reason -or $child.ExitCode -ne 0) { throw "Safe model $phase failed; inspect raw build logs" }
    } finally {
        if (-not $child.HasExited) { $child.Kill($true); if (-not $child.WaitForExit(10000)) { throw 'Build cleanup unconfirmed' } }
        $child.Dispose()
    }
}

$conversion = Join-Path $OutputDirectory 'conversion-manifest.json'
$infer = Join-Path $qa 'infer.py'
$config = @{
    python = $pythonBase; pythonSha256 = Hash $pythonBase
    sitePackages = (Resolve-Path (Join-Path $venv "Lib/site-packages")).Path
    interpreterLaunch = "Direct verified base interpreter; -I -S; explicit locked site-packages; no .pth execution"
    inferScript = $infer; inferScriptSha256 = Hash $infer
    conversionManifest = $conversion; conversionManifestSha256 = Hash $conversion
    source = Join-Path $repo 'scripts/ai-model-qa/inputs/source48.json'
    holdout = Join-Path $repo 'scripts/ai-model-qa/inputs/holdout12.json'
    output = $OutputDirectory; memoryMiB = 3072
    protocol = 'm2m100-12b-direct-v1'; sourceLanguagePolicy = 'Known fixture labels; no autodetection claim'
    semanticStatus = 'PENDING SEMANTIC REVIEW'; overallSeconds = 180; perHopSeconds = 60
    coldWarmBudget = 'Both passes together fit one active 180-second target budget'
}
$configPath = Join-Path $OutputDirectory 'qa-config.json'
$config | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $configPath
& dotnet restore (Join-Path $qa 'M2mQa.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked QA restore failed' }
& dotnet build (Join-Path $qa 'M2mQa.csproj') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'QA build failed' }
& dotnet (Join-Path $qa 'bin/Release/net10.0/M2mQa.dll') $configPath 2>&1 | Tee-Object (Join-Path $OutputDirectory 'qa-console.log')
if ($LASTEXITCODE -ne 0) { throw 'Candidate technical checks failed; inspect retained raw evidence' }
