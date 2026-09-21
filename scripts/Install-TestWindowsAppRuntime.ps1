# Installs only the signed runtime packages selected by the locked NuGet restore.
# The production app's self-contained deployment/bootstrap policy is unchanged.
[CmdletBinding()]
param([switch]$InspectOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
$assets = Get-Content (Join-Path $repoRoot 'src/DropSpace.App/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$runtimeKeys = @($assets.libraries.Keys | Where-Object { $_ -like 'Microsoft.WindowsAppSDK.Runtime/*' })
if ($runtimeKeys.Count -ne 1) { throw 'Expected one locked Microsoft.WindowsAppSDK.Runtime package; run locked restore first.' }
$runtimeKey = $runtimeKeys[0]
$lock = Get-Content (Join-Path $repoRoot 'src/DropSpace.App/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$lockedVersions = @($lock.dependencies.Values | ForEach-Object {
    if ($_.ContainsKey('Microsoft.WindowsAppSDK.Runtime')) { $_['Microsoft.WindowsAppSDK.Runtime'].resolved }
} | Sort-Object -Unique)
if ($lockedVersions.Count -ne 1 -or $runtimeKey -ne "Microsoft.WindowsAppSDK.Runtime/$($lockedVersions[0])") {
    throw 'Restored runtime does not match the committed lock file.'
}
$packageDirectories = @($assets.packageFolders.Keys | ForEach-Object {
    $candidate = Join-Path $_ $assets.libraries[$runtimeKey].path
    if (Test-Path $candidate -PathType Container) { $candidate }
})
if ($packageDirectories.Count -eq 0) { throw 'Locked runtime package is missing from NuGet package folders.' }
$msixRoot = Join-Path $packageDirectories[0] 'tools/MSIX/win10-x64'
$packages = @(
    'Microsoft.WindowsAppRuntime.2.msix',
    'Microsoft.WindowsAppRuntime.DDLM.2.msix',
    'Microsoft.WindowsAppRuntime.Main.2.msix',
    'Microsoft.WindowsAppRuntime.Singleton.2.msix'
)
Write-Host "Test runtime prerequisite: $runtimeKey (x64)"
foreach ($file in $packages) {
    $path = Join-Path $msixRoot $file
    $archive = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) { throw "Missing manifest: $file" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    $identity = $manifest.Package.Identity
    if ($identity.ProcessorArchitecture -ne 'x64' -or
        $identity.Publisher -ne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US') {
        throw "Unexpected runtime identity: $file"
    }
    $requiredVersion = [version]$identity.Version
    $installed = @(Get-AppxPackage -Name $identity.Name | Where-Object {
        $_.Publisher -eq $identity.Publisher -and $_.Architecture -eq 'X64' -and [version]$_.Version -ge $requiredVersion
    })
    if ($installed.Count -gt 0) {
        Write-Host "Ready: $($identity.Name) >= $requiredVersion"
        continue
    }
    if ($InspectOnly) {
        Write-Host "Would install: $($identity.Name) $requiredVersion"
        continue
    }
    Add-AppxPackage -Path $path -ErrorAction Stop
    $verified = @(Get-AppxPackage -Name $identity.Name | Where-Object {
        $_.Publisher -eq $identity.Publisher -and $_.Architecture -eq 'X64' -and [version]$_.Version -ge $requiredVersion
    })
    if ($verified.Count -eq 0) { throw "Runtime registration did not satisfy $($identity.Name) $requiredVersion." }
    Write-Host "Installed and verified: $($identity.Name) $requiredVersion"
}
if ($InspectOnly) { Write-Host 'Inspection complete; missing packages were not installed.' }
else { Write-Host 'Windows App Runtime prerequisites verified for native App tests.' }
