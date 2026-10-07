# One focused PR validation, followed by one final-main package producer.
param([switch]$ValidateOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -cne 'airanluo-dot/DropSpace' -or
    (Get-Content RELEASE_VERSION -Raw).Trim() -cne 'v0.3.1-beta.17' -or
    (& git rev-parse HEAD).Trim() -cne $env:GITHUB_SHA) { throw 'Exact isolated Beta17 checkout is required.' }
if ($ValidateOnly) {
    if ($env:GITHUB_EVENT_NAME -cne 'pull_request') { throw 'Focused validation must qualify the actual PR checkout.' }
    $evidence = [IO.Path]::GetFullPath('artifacts/beta17-validation')
    New-Item $evidence -ItemType Directory -Force | Out-Null
    & dotnet build src/DropSpace.App/DropSpace.App.csproj -c Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:AppxPackageSigningEnabled=false -p:GenerateAppxPackageOnBuild=false 2>&1 | Tee-Object "$evidence/app-build.txt"
    if ($LASTEXITCODE -ne 0) { throw 'Complete Windows App/XAML Release build failed.' }
    $filters = & node --input-type=module -e "import {focusedFilters} from './scripts/beta17-release-validation.mjs'; console.log(JSON.stringify(focusedFilters));" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the reviewed focused test filters.' }
    & dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj -c Release --no-restore --filter $filters.infrastructure --results-directory $evidence --logger 'trx;LogFileName=infrastructure.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Affected Infrastructure/CUDA compatibility tests failed.' }
    & dotnet test tests/DropSpace.App.Tests/DropSpace.App.Tests.csproj -c Release --no-restore -p:Platform=x64 -p:WindowsAppSdkDeploymentManagerInitialize=false --filter $filters.app --results-directory $evidence --logger 'trx;LogFileName=app.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Affected native-boundary App tests failed.' }
    & node scripts/beta17-release-validation.mjs record $evidence
    if ($LASTEXITCODE -ne 0) { throw 'Real PR validation evidence could not be bound.' }
    # PR-only caches cannot necessarily be restored by main. Retain these
    # unchanged existing bytes with the validation evidence for final packaging.
    Copy-Item artifacts/lyrics-language/lid.176.bin "$evidence/lid.176.bin"
    return
}
if ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne 'refs/heads/main') { throw 'Package production requires explicit dispatch on final main.' }
& node scripts/beta17-release-validation.mjs verify artifacts/beta17-pr-validation
if ($LASTEXITCODE -ne 0) { throw 'Successful identical-tree PR validation is required before packaging.' }
& ./scripts/Build-PortableExe.ps1 -NoRestore
& ./scripts/Build-UnsignedPackage.ps1 -NoRestore
& ./scripts/Build-IdentityPackage.ps1
& ./scripts/Build-Installer.ps1
Copy-Item artifacts/installer/DropSpaceSetup.exe artifacts/release/DropSpaceSetup.exe
$packages = @(Get-ChildItem artifacts/msix -Recurse -File -Filter *.msix)
if ($packages.Count -ne 1) { throw 'Expected one final MSIX package.' }
Copy-Item $packages[0].FullName artifacts/release/DropSpace-x64.msix
Copy-Item artifacts/identity/DropSpace.Identity.msix artifacts/release/DropSpace.Identity.msix
Copy-Item artifacts/cuda-runtime/win-x64/cuda-runtime-*.json artifacts/release
& ./scripts/Test-MsixSymbolPolicy.ps1 -ArtifactRoot artifacts/msix
& ./scripts/Test-PortableSmoke.ps1 -Language en-US -RuntimeInspectionOutput artifacts/runtime-inspection/portable.json
& ./scripts/Test-MusicVisualSmoke.ps1 -Language en-US
& ./scripts/Inspect-AiRuntimePayload.ps1 -MsixPath artifacts/release/DropSpace-x64.msix -OutputPath artifacts/runtime-inspection/msix.json
# Check the actual installed payload once in the disposable runner. This does
# not reuse historical compiler-input waivers or claim upgrade/lifecycle coverage.
$packageIdentity = { param($Path,$Name) [ordered]@{name=$Name;bytes=(Get-Item $Path).Length;sha256=(Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()} }
$installDirectory = Join-Path $env:RUNNER_TEMP ('DropSpace-beta17-payload-' + [guid]::NewGuid().ToString('N'))
try {
    $install = Start-Process -FilePath ([IO.Path]::GetFullPath('artifacts/release/DropSpaceSetup.exe')) -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$installDirectory`"",'/TASKS=') -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw 'Final installer payload extraction failed.' }
    $installed = & $packageIdentity (Join-Path $installDirectory 'DropSpace.exe') DropSpace.exe
    $portable = & $packageIdentity artifacts/release/DropSpace.exe DropSpace.exe
    if ($installed.bytes -ne $portable.bytes -or $installed.sha256 -cne $portable.sha256) { throw 'Final installer contains different portable bytes.' }
    [ordered]@{schemaVersion=1;kind='installer-payload';verificationScope='isolated installation and payload bytes only; upgrade/lifecycle not tested';package=(& $packageIdentity artifacts/release/DropSpaceSetup.exe DropSpaceSetup.exe);installedPortable=$installed} |
        ConvertTo-Json -Depth 8 | Set-Content artifacts/runtime-inspection/installer.json -Encoding utf8
} finally {
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (Test-Path $uninstaller) {
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) { throw 'Isolated payload uninstall failed.' }
    }
}
& node scripts/ai-runtime-publication.mjs record-bundle artifacts/release runtime-directory artifacts/ai-runtime/win-x64 artifacts/runtime-inspection/portable.json artifacts/runtime-inspection/msix.json artifacts/runtime-inspection/installer.json
if ($LASTEXITCODE -ne 0) { throw 'Final package runtime binding failed.' }
& ./scripts/New-UpdateManifest.ps1
& ./scripts/Test-UpdateManifest.ps1
$publicFiles = @('DropSpace.exe','DropSpaceSetup.exe','DropSpace-x64.msix','runtime-publication.json','cuda-runtime-download.json','cuda-runtime-manifest.json','update-manifest.json')
$checksums = foreach ($name in $publicFiles) { "$( (Get-FileHash (Join-Path 'artifacts/release' $name) -Algorithm SHA256).Hash.ToLowerInvariant() )  $name" }
$checksums | Set-Content artifacts/release/SHA256SUMS.txt -Encoding ascii
& node scripts/test-ai-release-approval.mjs --release-bundle artifacts/release
if ($LASTEXITCODE -ne 0) { throw 'Final source/runtime publication approval failed.' }
& node scripts/ci-release-promotion.mjs record artifacts/release
if ($LASTEXITCODE -ne 0) { throw 'Final-main release promotion receipt failed.' }
