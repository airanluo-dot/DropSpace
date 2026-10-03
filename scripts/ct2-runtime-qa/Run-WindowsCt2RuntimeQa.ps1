[CmdletBinding()]
param([Parameter(Mandatory)][string]$Inputs, [Parameter(Mandatory)][string]$Work, [Parameter(Mandatory)][string]$Output)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$IsWindows -or ![Environment]::Is64BitProcess -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'Windows x64 PowerShell required.' }
$Inputs = [IO.Path]::GetFullPath($Inputs)
$Work = [IO.Path]::GetFullPath($Work)
$Output = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $Work) -or (Test-Path -LiteralPath $Output)) { throw 'Work and output directories must be new.' }
New-Item -ItemType Directory -Path $Work, $Output | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Output 'licenses') | Out-Null
$status = @{ status='RUNNING'; productionReady=$false; modelQuality='NOT TESTED'; redistributable='NOT APPROVED' }
function Save-Status { $status | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $Output 'status.json') }
Save-Status
Start-Transcript -LiteralPath (Join-Path $Output 'build-transcript.txt') | Out-Null
try {
    $source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'provenance.json') | ConvertFrom-Json
    $lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') | ConvertFrom-Json
    foreach ($file in @('dependencies.json', 'provenance.json')) {
        if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $file)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $Inputs $file)).Hash) { throw 'Input lock differs from reviewed repository lock.' }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $Output
    }
    Copy-Item -Path (Join-Path $Inputs 'licenses/*') -Destination (Join-Path $Output 'licenses')
    $installer = Join-Path $Inputs $source.pythonInstaller.file
    if ((Get-Item -LiteralPath $installer).Length -ne $source.pythonInstaller.bytes -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant() -cne $source.pythonInstaller.sha256) { throw 'CPython installer hash mismatch.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Python Software Foundation(,|$)') { throw 'Official CPython signature is not valid.' }
    @{ installerSha256=$source.pythonInstaller.sha256; signer=$signature.SignerCertificate.Subject; thumbprint=$signature.SignerCertificate.Thumbprint; status=[string]$signature.Status } |
        ConvertTo-Json | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $Output 'python-signature.json')
    # Ephemeral per-user private builder only. Do not register a launcher, PATH, file associations,
    # shortcuts, service, machine-wide runtime, or alter security/network policy.
    $pythonHome = Join-Path $Work 'python312'
    $install = Start-Process -FilePath $installer -ArgumentList @('/quiet', 'InstallAllUsers=0', "TargetDir=`"$pythonHome`"", 'PrependPath=0', 'AppendPath=0', 'AssociateFiles=0', 'Shortcuts=0', 'Include_launcher=0', 'InstallLauncherAllUsers=0', 'Include_test=0', 'Include_doc=0', 'Include_tcltk=0', 'Include_tools=0', 'Include_dev=0', 'Include_symbols=0', 'Include_debug=0', 'Include_pip=1', 'CompileAll=0', '/log', "`"$(Join-Path $Output 'python-install.log')`"") -Wait -PassThru -WindowStyle Hidden
    if ($install.ExitCode -ne 0) { throw "Private CPython install failed: $($install.ExitCode)" }
    $python = Join-Path $pythonHome 'python.exe'
    $pythonHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $python).Hash.ToLowerInvariant()
    $pythonVersion = & $python -I -S -c 'import sys; print(".".join(map(str,sys.version_info[:3])))'
    if ($LASTEXITCODE -ne 0 -or $pythonVersion -cne $lock.python) { throw 'CPython version mismatch.' }
    @{ version=$pythonVersion; executableSha256=$pythonHash; trust='Pinned official full installer plus Authenticode; executable hash is recorded, not the sole trust anchor.' } |
        ConvertTo-Json | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $Output 'python-builder.json')
    Copy-Item -LiteralPath (Join-Path $pythonHome 'LICENSE.txt') -Destination (Join-Path $Output 'licenses/python-LICENSE.txt')
    $runtime = Join-Path $Output 'candidate'
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $sourceFiles = @('tools/ct2-helper/helper.py', 'tools/ct2-helper/build.ps1',
        'src/DropSpace.Infrastructure/Lyrics/WindowsInferenceProcess.cs', 'src/DropSpace.Infrastructure/Lyrics/LocalInferenceProcess.cs',
        'scripts/ct2-runtime-qa/Program.cs', 'scripts/ct2-runtime-qa/inspect_runtime.py',
        'scripts/ct2-runtime-qa/Run-WindowsCt2RuntimeQa.ps1', 'scripts/ct2-runtime-qa/Get-VerifiedInputs.ps1')
    @{ commit=$env:GITHUB_SHA; files=@($sourceFiles | ForEach-Object {
        @{ path=$_; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant() }
    }) } | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $Output 'source-inputs.json')
    & (Join-Path $repo 'tools/ct2-helper/build.ps1') -PythonExecutable $python -PythonSha256 $pythonHash -Wheelhouse (Join-Path $Inputs 'wheelhouse') -Inventory (Join-Path $PSScriptRoot 'dependencies.json') -Output $runtime
    # Independent offline inspector environment; the application never receives this interpreter.
    $venv = Join-Path $Work 'inspector'
    & $python -I -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Inspector environment failed.' }
    $inspectPython = Join-Path $venv 'Scripts/python.exe'
    $requirements = foreach ($package in $lock.packages) {
        $path = [Uri](Join-Path $Inputs ('wheelhouse/' + $package.file))
        "$($package.name) @ $($path.AbsoluteUri) --hash=sha256:$($package.sha256)"
    }
    $requirements | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $Work 'inspector-requirements.txt')
    & $inspectPython -I -m pip --isolated --disable-pip-version-check install --no-index --no-deps --no-cache-dir --only-binary=:all: --require-hashes -r (Join-Path $Work 'inspector-requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Inspector offline install failed.' }
    & $inspectPython -I -m pip --isolated --disable-pip-version-check check
    if ($LASTEXITCODE -ne 0) { throw 'Inspector dependency closure failed.' }
    & $inspectPython -I (Join-Path $PSScriptRoot 'inspect_runtime.py') $Inputs $runtime $Output
    if ($LASTEXITCODE -ne 0) { throw 'Native import/PE/license evidence failed.' }
    & dotnet run --project (Join-Path $PSScriptRoot 'Ct2RuntimeQa.csproj') --configuration Release -- $runtime $Output
    if ($LASTEXITCODE -ne 0) { throw 'Actual frozen helper native smoke failed.' }
    $status.status = 'PASSED_PRIVATE_SMOKE_ONLY'
} catch {
    $status.status = 'FAILED'
    $status.error = $_.ToString()
    throw
} finally {
    Save-Status
    Stop-Transcript | Out-Null
    # Complete retained-artifact inventory, with the inventory itself necessarily excluded.
    $files = @(Get-ChildItem -LiteralPath $Output -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{ path=[IO.Path]::GetRelativePath($Output, $_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
    })
    @{ schemaVersion=1; files=$files } | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $Output 'evidence-files.json')
}
