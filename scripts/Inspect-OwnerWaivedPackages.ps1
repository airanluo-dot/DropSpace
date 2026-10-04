# Exact owner-authorized owner-waived packaging-only lane; no functional test verdict.
param([ValidateSet('Installer','Portable')][string]$Kind)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (@('v0.3.1-beta.6','v0.3.1-beta.7') -cnotcontains (Get-Content RELEASE_VERSION -Raw).Trim()) { throw 'Packaging-only exception is limited to explicitly waived releases.' }
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Use only the isolated CI runner.' }
function Identity([string]$Path,[string]$Name) {
    return [ordered]@{name=$Name;bytes=(Get-Item -LiteralPath $Path).Length;sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$exe=[IO.Path]::GetFullPath('artifacts/release/DropSpace.exe')
if ($Kind -eq 'Installer') {
    $installer=[IO.Path]::GetFullPath('artifacts/installer/DropSpaceSetup.exe')
    $dest=Join-Path $env:RUNNER_TEMP ('owner-waived-payload-'+[Guid]::NewGuid().ToString('N'))
    try {
        $p=Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$dest`"",'/TASKS=') -PassThru -Wait
        if ($p.ExitCode -ne 0) { throw 'Installer extraction failed.' }
        $installed=Identity (Join-Path $dest 'DropSpace.exe') 'DropSpace.exe'
        $original=Identity $exe 'DropSpace.exe'
        if ($installed.sha256 -cne $original.sha256 -or $installed.bytes -ne $original.bytes) { throw 'Installer payload differs from built executable.' }
        $report=[ordered]@{schemaVersion=1;kind='installer-payload';verificationScope='payload-bytes-only; lifecycle tests waived';package=(Identity $installer 'DropSpaceSetup.exe');installedPortable=$installed}
        New-Item artifacts/runtime-inspection -ItemType Directory -Force | Out-Null
        $report | ConvertTo-Json -Depth 10 | Set-Content artifacts/runtime-inspection/installer.json -Encoding utf8
    } finally {
        $uninstaller=Join-Path $dest 'unins000.exe'
        if(Test-Path $uninstaller) { Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait }
    }
} else {
    $root=Join-Path $env:RUNNER_TEMP ('owner-waived-bundle-'+[Guid]::NewGuid().ToString('N'))
    $old=$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    $p=$null
    try {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$root
        # Only let the .NET host unpack its managed payload; do not run smoke fixtures.
        $p=Start-Process -FilePath $exe -ArgumentList '--startup' -PassThru
        $deadline=(Get-Date).AddSeconds(60)
        do {
            $assemblies=@(Get-ChildItem $root -Recurse -File -Filter DropSpace.dll -ErrorAction SilentlyContinue)
            if ($assemblies.Count -eq 1) { break }
            if ((Get-Date) -gt $deadline) { throw 'Bundle extraction timed out.' }
            Start-Sleep -Milliseconds 100
        } while ($true)
        if (!$p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit() }
        ./scripts/Inspect-AiRuntimePayload.ps1 -AssemblyPath $assemblies[0].FullName -OutputPath artifacts/release/portable-inspection.json
        $report=Get-Content artifacts/release/portable-inspection.json -Raw | ConvertFrom-Json -AsHashtable
        $report.package=Identity $exe 'DropSpace.exe'
        $report.verificationScope='unpacked runtime bytes only; startup and UI tests waived'
        $report | ConvertTo-Json -Depth 20 | Set-Content artifacts/release/portable-inspection.json -Encoding utf8
    } finally {
        if($null -ne $p -and !$p.HasExited) { Stop-Process -Id $p.Id -Force }
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$old
    }
}
