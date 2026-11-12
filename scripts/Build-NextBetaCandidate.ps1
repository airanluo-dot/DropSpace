# New Beta PR compilation followed by one exact-main package producer; no unit, browser or model cases.
param([switch]$ValidateOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -cne 'airanluo-dot/DropSpace' -or
    (& git rev-parse HEAD).Trim() -cne $env:GITHUB_SHA) { throw 'Exact isolated new-Beta checkout is required.' }
& node --input-type=module -e "import fs from 'node:fs'; import {isNextBetaRelease} from './scripts/next-beta-release-validation.mjs'; if (!isNextBetaRelease(fs.readFileSync('RELEASE_VERSION','utf8').trim())) process.exit(1);"
if ($LASTEXITCODE -ne 0) { throw 'A dynamically selected new Beta version is required.' }
& node scripts/test-release-metadata.mjs $PWD
if ($LASTEXITCODE -ne 0) { throw 'Release version, notes and current product metadata disagree.' }
# Validate the same updater-summary contract before App compilation, rather than
# discovering a missing marker only after all final packages have been built.
. (Join-Path $PSScriptRoot 'ReleaseNotes.ps1')
$null = Get-DropSpaceUpdateSummary -RepositoryRoot $PWD.Path -Tag ((Get-Content RELEASE_VERSION -Raw).Trim())
& ./scripts/Test-Localization.ps1 -Scope app
if ($LASTEXITCODE -ne 0) { throw 'Translation completeness blocks compilation and publication.' }
if ($ValidateOnly) {
    if ($env:GITHUB_EVENT_NAME -cne 'pull_request') { throw 'Focused validation must qualify the actual PR checkout.' }
    $evidence = [IO.Path]::GetFullPath('artifacts/next-beta-validation')
    New-Item $evidence -ItemType Directory -Force | Out-Null
    & dotnet build src/DropSpace.App/DropSpace.App.csproj -c Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:AppxPackageSigningEnabled=false -p:GenerateAppxPackageOnBuild=false 2>&1 | Tee-Object "$evidence/app-build.txt"
    if ($LASTEXITCODE -ne 0) { throw 'Complete Windows App/XAML Release build failed.' }
    & node scripts/next-beta-release-validation.mjs record $evidence
    if ($LASTEXITCODE -ne 0) { throw 'Real PR compilation evidence could not be bound.' }
    # PR-only caches cannot necessarily be restored by main. Retain these
    # unchanged existing bytes with the validation evidence for final packaging.
    Copy-Item artifacts/lyrics-language/lid.176.bin "$evidence/lid.176.bin"
    return
}
if ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne 'refs/heads/main') { throw 'Package production requires explicit dispatch on final main.' }
& node scripts/next-beta-release-validation.mjs verify artifacts/next-beta-pr-validation
if ($LASTEXITCODE -ne 0) { throw 'Successful identical-tree PR validation is required before packaging.' }
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
& ./scripts/Inspect-ReleasePackage.ps1 -Kind Portable -OutputPath artifacts/runtime-inspection/portable.json
& ./scripts/Inspect-AiRuntimePayload.ps1 -MsixPath artifacts/release/DropSpace-x64.msix -OutputPath artifacts/runtime-inspection/msix.json
& ./scripts/Inspect-ReleasePackage.ps1 -Kind Installer -InstallerPath artifacts/release/DropSpaceSetup.exe
& node scripts/ai-runtime-publication.mjs record-bundle artifacts/release runtime-directory artifacts/ai-runtime/win-x64 artifacts/runtime-inspection/portable.json artifacts/runtime-inspection/msix.json artifacts/runtime-inspection/installer.json
if ($LASTEXITCODE -ne 0) { throw 'Final package runtime binding failed.' }
& ./scripts/New-UpdateManifest.ps1
& ./scripts/Test-UpdateManifest.ps1
& ./scripts/Test-PackagedLocalization.ps1 -ArtifactDirectory artifacts/release
if ($LASTEXITCODE -ne 0) { throw 'Actual package language resources are incomplete.' }
$publicFiles = @('DropSpace.exe','DropSpaceSetup.exe','DropSpace-x64.msix','runtime-publication.json','cuda-runtime-download.json','cuda-runtime-manifest.json','update-manifest.json')
$checksums = foreach ($name in $publicFiles) { "$( (Get-FileHash (Join-Path 'artifacts/release' $name) -Algorithm SHA256).Hash.ToLowerInvariant() )  $name" }
$checksums | Set-Content artifacts/release/SHA256SUMS.txt -Encoding ascii
& node scripts/test-ai-release-approval.mjs --release-bundle artifacts/release
if ($LASTEXITCODE -ne 0) { throw 'Final source/runtime publication approval failed.' }
& node scripts/ci-release-promotion.mjs record artifacts/release
if ($LASTEXITCODE -ne 0) { throw 'Final-main release promotion receipt failed.' }
