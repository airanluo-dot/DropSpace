param(
    [Parameter(Mandatory)][ValidateSet('Installer', 'Portable')][string]$Kind,
    [string]$InstallerPath = 'artifacts/installer/DropSpaceSetup.exe',
    [string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Package inspection requires an isolated CI runner.' }

function Get-PackageIdentity([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    return [ordered]@{
        name = $file.Name
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Invoke-SilentSetup([string]$Path, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait
    try {
        if ($process.ExitCode -ne 0) { throw "$Path exited with code $($process.ExitCode)." }
    } finally {
        $process.Dispose()
    }
}

$portablePath = [IO.Path]::GetFullPath('artifacts/release/DropSpace.exe')
$portableIdentity = Get-PackageIdentity $portablePath
if (-not $OutputPath) {
    $OutputPath = if ($Kind -eq 'Installer') {
        'artifacts/runtime-inspection/installer.json'
    } else {
        'artifacts/release/portable-inspection.json'
    }
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -Path (Split-Path -Parent $OutputPath) -ItemType Directory -Force | Out-Null

if ($Kind -eq 'Portable') {
    $scratchDirectory = Join-Path $env:RUNNER_TEMP ('dropspace-bundle-' + [Guid]::NewGuid())
    New-Item -Path $scratchDirectory -ItemType Directory | Out-Null
    $assemblyPath = Join-Path $scratchDirectory 'DropSpace.dll'
    try {
        & ./scripts/Extract-StaticBundleAssembly.ps1 -PortablePath $portablePath -OutputPath $assemblyPath
        & ./scripts/Inspect-AiRuntimePayload.ps1 -AssemblyPath $assemblyPath -OutputPath $OutputPath
        $report = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json -AsHashtable
        $report.package = $portableIdentity
        $report.verificationScope = 'static single-file bundle payload inspection'
    } finally {
        if (Test-Path -LiteralPath $assemblyPath) { Remove-Item -LiteralPath $assemblyPath -Force }
        Remove-Item -LiteralPath $scratchDirectory
    }
} else {
    $InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
    $report = [ordered]@{
        schemaVersion = 1
        package = Get-PackageIdentity $InstallerPath
    }
    $releaseVersion = (Get-Content -LiteralPath RELEASE_VERSION -Raw).Trim()
    if ($releaseVersion -cin @('v0.3.1-beta.11', 'v0.3.1-beta.15', 'v0.3.1-beta.16')) {
        # These historical receipts cover the compiler input only.
        $report.kind = 'installer-build-input'
        $report.verificationScope = 'compiler-input-bytes-only; installer not executed; tests waived'
        $report.buildInputPortable = $portableIdentity
    } else {
        $installDirectory = Join-Path $env:RUNNER_TEMP ('dropspace-install-' + [Guid]::NewGuid())
        $silentFlags = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        $installFlags = $silentFlags + @(('/DIR="{0}"' -f $installDirectory), '/TASKS=')
        try {
            Invoke-SilentSetup $InstallerPath $installFlags
            $installedPortable = Get-PackageIdentity (Join-Path $installDirectory 'DropSpace.exe')
            if ($installedPortable.sha256 -cne $portableIdentity.sha256 -or $installedPortable.bytes -ne $portableIdentity.bytes) {
                throw 'The installed executable differs from the release portable executable.'
            }
            $report.kind = 'installer-payload'
            $report.verificationScope = 'installed portable size and SHA-256 match the packaged executable'
            $report.installedPortable = $installedPortable
        } finally {
            $uninstaller = Join-Path $installDirectory 'uninstall/unins000.exe'
            if (-not (Test-Path -LiteralPath $uninstaller)) { $uninstaller = Join-Path $installDirectory 'unins000.exe' }
            if (Test-Path -LiteralPath $uninstaller) { Invoke-SilentSetup $uninstaller $silentFlags }
        }
    }
}
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $OutputPath -Encoding utf8
