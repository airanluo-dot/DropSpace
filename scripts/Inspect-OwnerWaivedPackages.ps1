# Exact owner-authorized owner-waived packaging-only lane; no functional test verdict.
param([ValidateSet('Installer','Portable')][string]$Kind)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (@('v0.3.1-beta.6','v0.3.1-beta.7','v0.3.1-beta.8','v0.3.1-beta.9','v0.3.1-beta.10','v0.3.1-beta.11','v0.3.1-beta.15') -cnotcontains (Get-Content RELEASE_VERSION -Raw).Trim()) { throw 'Packaging-only exception is limited to explicitly waived releases.' }
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Use only the isolated CI runner.' }
function Identity([string]$Path,[string]$Name) {
    return [ordered]@{name=$Name;bytes=(Get-Item -LiteralPath $Path).Length;sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$exe=[IO.Path]::GetFullPath('artifacts/release/DropSpace.exe')
if ($Kind -eq 'Installer') {
    $installer=[IO.Path]::GetFullPath('artifacts/installer/DropSpaceSetup.exe')
    if (@('v0.3.1-beta.11','v0.3.1-beta.15') -ccontains (Get-Content RELEASE_VERSION -Raw).Trim()) {
        $report=[ordered]@{schemaVersion=1;kind='installer-build-input';verificationScope='compiler-input-bytes-only; installer not executed; tests waived';package=(Identity $installer 'DropSpaceSetup.exe');buildInputPortable=(Identity $exe 'DropSpace.exe')}
        New-Item artifacts/runtime-inspection -ItemType Directory -Force | Out-Null
        $report | ConvertTo-Json -Depth 10 | Set-Content artifacts/runtime-inspection/installer.json -Encoding utf8
        return
    }
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
    New-Item $root -ItemType Directory | Out-Null
    $assembly=Join-Path $root 'DropSpace.dll'
    try {
        ./scripts/Extract-StaticBundleAssembly.ps1 -PortablePath $exe -OutputPath $assembly
        ./scripts/Inspect-AiRuntimePayload.ps1 -AssemblyPath $assembly -OutputPath artifacts/release/portable-inspection.json
        $report=Get-Content artifacts/release/portable-inspection.json -Raw | ConvertFrom-Json -AsHashtable
        $report.package=Identity $exe 'DropSpace.exe'
        $report.verificationScope='static bundle bytes only; executable never launched; tests waived'
        $report | ConvertTo-Json -Depth 20 | Set-Content artifacts/release/portable-inspection.json -Encoding utf8
    } finally {
        if(Test-Path -LiteralPath $assembly){Remove-Item -LiteralPath $assembly -Force}
        if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Force}
    }
}
