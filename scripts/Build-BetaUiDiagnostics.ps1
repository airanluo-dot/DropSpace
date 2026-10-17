# Builds only a Debug unpackaged application for the two explicitly selected UI diagnostics.
[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/beta-ui-diagnostics/build')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native Beta UI diagnostics require Windows.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) { $OutputDirectory } else { Join-Path $repo $OutputDirectory }))
if (Test-Path -LiteralPath $output) { throw 'The build evidence directory must be fresh.' }
New-Item -ItemType Directory -Path $output | Out-Null
$receipt = [ordered]@{
    schemaVersion = 1; kind = 'windows-debug-native-ui-build'; status = 'failed'; stage = 'source-identity'
    sourceCommit = $null; sourceTree = $null; executablePath = $null; executableSha256 = $null
    resourceIndexSha256 = $null; managedAssemblyHashes = @{}; startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); finishedAtUtc = $null
    configuration = 'Debug'; deployment = 'unpackaged-framework-dependent'; releasePromotionEligible = $false
    functionalScenarioExecutions = 0; failure = $null
}

Push-Location $repo
try {
    $receipt.sourceCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the source commit.' }
    $receipt.sourceTree = (& git rev-parse 'HEAD^{tree}').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the source tree.' }
    $changes = @(& git status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Commit the diagnostic sources before building an identifiable executable.' }
    if ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $receipt.sourceCommit) { throw 'The checkout does not match the workflow source commit.' }
    foreach ($directory in @('artifacts/ai-runtime', 'artifacts/cuda-runtime', 'artifacts/lyrics-language')) {
        if (Test-Path -LiteralPath $directory) { throw "Diagnostic checkout must not contain staged model/runtime assets: $directory" }
    }

    $project = 'src/DropSpace.App/DropSpace.App.csproj'
    $properties = @(
        '-p:Configuration=Debug', '-p:Platform=x64', '-p:RuntimeIdentifier=win-x64',
        '-p:DropSpaceDeployment=Portable', '-p:WindowsPackageType=None',
        '-p:SelfContained=false', '-p:WindowsAppSDKSelfContained=false', '-p:PublishSingleFile=false',
        '-p:WindowsAppSdkBootstrapInitialize=true', '-p:WindowsAppSDKBootstrapAutoInitializeOptions_None=true',
        '-p:WindowsAppSdkDeploymentManagerInitialize=false',
        '-p:GenerateAppxPackageOnBuild=false', '-p:AppxPackageSigningEnabled=false'
    )
    $receipt.stage = 'locked-app-restore'
    # The App already defaults to win-x64. A global RID during restore would also
    # propagate into Core/Infrastructure and invalidate their RID-free lockfiles.
    $restoreProperties = @($properties | Where-Object { $_ -notlike '-p:RuntimeIdentifier=*' })
    & dotnet restore $project @restoreProperties -p:RestoreLockedMode=true 2>&1 | Tee-Object (Join-Path $output 'restore.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Locked Debug App restore failed.' }
    $receipt.stage = 'windows-app-runtime-registration'
    & (Join-Path $PSScriptRoot 'Install-TestWindowsAppRuntime.ps1') 2>&1 | Tee-Object (Join-Path $output 'windows-runtime.txt')
    $receipt.stage = 'debug-app-xaml-build'
    & dotnet build $project -c Debug --no-restore @properties "-bl:$(Join-Path $output 'app-build.binlog')" 2>&1 | Tee-Object (Join-Path $output 'app-build.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Debug unpackaged App/XAML compilation failed.' }
    $targetDirectory = (& dotnet msbuild $project @properties -getProperty:TargetDir | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not [IO.Path]::IsPathFullyQualified($targetDirectory) -or
        -not (Test-Path -LiteralPath $targetDirectory -PathType Container)) {
        throw 'Could not resolve the actual Debug application output directory.'
    }
    $exe = Join-Path $targetDirectory 'DropSpace.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The Debug build did not produce its executable.' }
    $receipt.stage = 'explicit-app-resource-index'
    $pri = Join-Path $targetDirectory 'DropSpace.resources.pri'
    & (Join-Path $PSScriptRoot 'Generate-PortableResourcesPri.ps1') -ProjectRoot (Join-Path $repo 'src/DropSpace.App') -OutputPath $pri -ConfigurationPath (Join-Path $output 'resources.priconfig.xml') 2>&1 | Tee-Object (Join-Path $output 'resources.txt')
    if (-not (Test-Path -LiteralPath $pri -PathType Leaf)) { throw 'The unpackaged application resource index was not produced.' }
    $receipt.executablePath = $exe
    $receipt.executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $receipt.resourceIndexSha256 = (Get-FileHash -LiteralPath $pri -Algorithm SHA256).Hash.ToLowerInvariant()
    # A framework-dependent EXE is an apphost; bind the actual managed application bytes too.
    foreach ($assembly in @('DropSpace.dll', 'DropSpace.Core.dll', 'DropSpace.Infrastructure.dll')) {
        $receipt.managedAssemblyHashes[$assembly] = (Get-FileHash -LiteralPath (Join-Path $targetDirectory $assembly) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $receipt.status = 'passed'; $receipt.stage = 'build-ready'
}
catch {
    $receipt.failure = $_.Exception.Message
    throw
}
finally {
    $receipt.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $receipt | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'build-receipt.json') -Encoding utf8
    Write-Output ('DROPSPACE_BETA_UI_BUILD=' + ($receipt | ConvertTo-Json -Depth 5 -Compress))
    Pop-Location
}
