# Beta18 PR compilation only, followed by one exact-main focused package producer.
param([switch]$ValidateOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -cne 'airanluo-dot/DropSpace' -or
    (Get-Content RELEASE_VERSION -Raw).Trim() -cne 'v0.3.1-beta.18' -or
    (& git rev-parse HEAD).Trim() -cne $env:GITHUB_SHA) { throw 'Exact isolated Beta18 checkout is required.' }
if ($ValidateOnly) {
    if ($env:GITHUB_EVENT_NAME -cne 'pull_request') { throw 'Focused validation must qualify the actual PR checkout.' }
    $evidence = [IO.Path]::GetFullPath('artifacts/beta18-validation')
    New-Item $evidence -ItemType Directory -Force | Out-Null
    & dotnet build src/DropSpace.App/DropSpace.App.csproj -c Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:AppxPackageSigningEnabled=false -p:GenerateAppxPackageOnBuild=false 2>&1 | Tee-Object "$evidence/app-build.txt"
    if ($LASTEXITCODE -ne 0) { throw 'Complete Windows App/XAML Release build failed.' }
    & node scripts/beta18-release-validation.mjs record $evidence
    if ($LASTEXITCODE -ne 0) { throw 'Real PR validation evidence could not be bound.' }
    # PR-only caches cannot necessarily be restored by main. Retain these
    # unchanged existing bytes with the validation evidence for final packaging.
    Copy-Item artifacts/lyrics-language/lid.176.bin "$evidence/lid.176.bin"
    return
}
if ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne 'refs/heads/main') { throw 'Package production requires explicit dispatch on final main.' }
& node scripts/beta18-release-validation.mjs verify artifacts/beta18-pr-validation
if ($LASTEXITCODE -ne 0) { throw 'Successful identical-tree PR validation is required before packaging.' }
$evidence = [IO.Path]::GetFullPath('artifacts/beta18-validation')
New-Item $evidence -ItemType Directory -Force | Out-Null
$focused = & node --input-type=module -e "import {focusedFilter,focusedProject,focusedCaseCount,originalSuiteCaseCount} from './scripts/beta18-release-validation.mjs'; if (!focusedFilter || focusedCaseCount <= 0 || focusedCaseCount > Math.floor(originalSuiteCaseCount / 100)) process.exit(1); console.log(JSON.stringify({filter:focusedFilter,project:focusedProject}));" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Exact budgeted focused cases are required; no full-suite fallback.' }
& dotnet restore $focused.project -p:Configuration=Release -p:RestoreLockedMode=true -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Focused test project locked restore failed.' }
& dotnet test $focused.project -c Release --no-restore -p:Platform=x64 --filter $focused.filter --results-directory $evidence --logger 'trx;LogFileName=focused.trx'
if ($LASTEXITCODE -ne 0) { throw 'Necessary focused cases failed; no automatic retest or broad fallback.' }
& node scripts/beta18-release-validation.mjs record-focused $evidence
if ($LASTEXITCODE -ne 0) { throw 'Actual focused execution count/results could not be bound.' }
& ./scripts/Build-PortableExe.ps1 -NoRestore
& ./scripts/Build-UnsignedPackage.ps1 -NoRestore
& ./scripts/Build-IdentityPackage.ps1
& ./scripts/Build-Installer.ps1
Copy-Item artifacts/installer/DropSpaceSetup.exe artifacts/release/DropSpaceSetup.exe
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$releaseInfo = Get-DropSpaceReleaseInfo ((Get-Content RELEASE_VERSION -Raw).Trim())
$expectedPrefix = "DropSpace.App_$($releaseInfo.PackageVersion)_"
# The SDK also emits dependency packages. Match the same current App identity
# already validated by Build-UnsignedPackage, including its AppX compatibility.
$packages = @(Get-ChildItem artifacts/msix -Recurse -File | Where-Object {
    $_.Extension -in '.msix', '.appx' -and $_.Name.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)
})
if ($packages.Count -ne 1) { throw 'Expected one current DropSpace MSIX/AppX package.' }
Copy-Item $packages[0].FullName artifacts/release/DropSpace-x64.msix
Copy-Item artifacts/identity/DropSpace.Identity.msix artifacts/release/DropSpace.Identity.msix
Copy-Item artifacts/cuda-runtime/win-x64/cuda-runtime-*.json artifacts/release
& ./scripts/Test-MsixSymbolPolicy.ps1 -ArtifactRoot artifacts/msix
# Inspect the actual final single-file bytes without launching the App or running
# the legacy smoke's thousands of lifecycle/stress cases.
$portableAssembly = [IO.Path]::GetFullPath('artifacts/runtime-inspection/DropSpace.dll')
New-Item (Split-Path $portableAssembly -Parent) -ItemType Directory -Force | Out-Null
& ./scripts/Extract-StaticBundleAssembly.ps1 -PortablePath artifacts/release/DropSpace.exe -OutputPath $portableAssembly
& ./scripts/Inspect-AiRuntimePayload.ps1 -AssemblyPath $portableAssembly -OutputPath artifacts/runtime-inspection/portable.json
$inspection = Get-Content artifacts/runtime-inspection/portable.json -Raw | ConvertFrom-Json -AsHashtable
$inspection.package = [ordered]@{name='DropSpace.exe';bytes=(Get-Item artifacts/release/DropSpace.exe).Length;sha256=(Get-FileHash artifacts/release/DropSpace.exe -Algorithm SHA256).Hash.ToLowerInvariant()}
$inspection.verificationScope = 'actual single-file manifest/embedded bytes only; App startup and native smoke/stress not executed'
$inspection | ConvertTo-Json -Depth 8 | Set-Content artifacts/runtime-inspection/portable.json -Encoding utf8
Remove-Item -LiteralPath $portableAssembly
& ./scripts/Inspect-AiRuntimePayload.ps1 -MsixPath artifacts/release/DropSpace-x64.msix -OutputPath artifacts/runtime-inspection/msix.json
# Inspect actual installed payload once in the disposable runner; this is
# artifact identity verification, not upgrade/lifecycle or functional coverage.
$packageIdentity = { param($Path,$Name) [ordered]@{name=$Name;bytes=(Get-Item $Path).Length;sha256=(Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()} }
$installDirectory = Join-Path $env:RUNNER_TEMP ('DropSpace-beta18-payload-' + [guid]::NewGuid().ToString('N'))
try {
    $install = Start-Process -FilePath ([IO.Path]::GetFullPath('artifacts/release/DropSpaceSetup.exe')) -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$installDirectory`"",'/TASKS=') -Wait -PassThru
    $installExitCode = $install.ExitCode
    $install.Dispose()
    if ($installExitCode -ne 0) { throw 'Final installer payload extraction failed.' }
    $installed = & $packageIdentity (Join-Path $installDirectory 'DropSpace.exe') DropSpace.exe
    $portable = & $packageIdentity artifacts/release/DropSpace.exe DropSpace.exe
    if ($installed.bytes -ne $portable.bytes -or $installed.sha256 -cne $portable.sha256) { throw 'Final installer contains different portable bytes.' }
    [ordered]@{schemaVersion=1;kind='installer-payload';verificationScope='isolated installation and payload bytes only; upgrade/lifecycle not tested';package=(& $packageIdentity artifacts/release/DropSpaceSetup.exe DropSpaceSetup.exe);installedPortable=$installed} |
        ConvertTo-Json -Depth 8 | Set-Content artifacts/runtime-inspection/installer.json -Encoding utf8
} finally {
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (Test-Path $uninstaller) {
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
        $uninstallExitCode = $uninstall.ExitCode
        $uninstall.Dispose()
        if ($uninstallExitCode -ne 0) { throw 'Isolated payload uninstall failed.' }
    }
}
& node scripts/ai-runtime-publication.mjs record-bundle artifacts/release runtime-directory artifacts/ai-runtime/win-x64 artifacts/runtime-inspection/portable.json artifacts/runtime-inspection/msix.json artifacts/runtime-inspection/installer.json
if ($LASTEXITCODE -ne 0) { throw 'Final package runtime binding failed.' }
& ./scripts/New-UpdateManifest.ps1
& ./scripts/Test-UpdateManifest.ps1
Copy-Item "$evidence/focused.trx", "$evidence/focused-validation.json" artifacts/release
$publicFiles = @('DropSpace.exe','DropSpaceSetup.exe','DropSpace-x64.msix','runtime-publication.json','cuda-runtime-download.json','cuda-runtime-manifest.json','update-manifest.json')
$checksums = foreach ($name in $publicFiles) { "$( (Get-FileHash (Join-Path 'artifacts/release' $name) -Algorithm SHA256).Hash.ToLowerInvariant() )  $name" }
$checksums | Set-Content artifacts/release/SHA256SUMS.txt -Encoding ascii
& node scripts/test-ai-release-approval.mjs --release-bundle artifacts/release
if ($LASTEXITCODE -ne 0) { throw 'Final source/runtime publication approval failed.' }
& node scripts/ci-release-promotion.mjs record artifacts/release
if ($LASTEXITCODE -ne 0) { throw 'Final-main release promotion receipt failed.' }
