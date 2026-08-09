param(
    [string]$InstallDirectory = "artifacts/tools/inno-7.0.2"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedInstallDirectory = if ([System.IO.Path]::IsPathRooted($InstallDirectory))
{
    $InstallDirectory
}
else
{
    Join-Path $repositoryRoot $InstallDirectory
}
$compiler = Join-Path $resolvedInstallDirectory "ISCC.exe"
if (Test-Path $compiler -PathType Leaf)
{
    Write-Output $compiler
    exit 0
}

$downloadUrl = "https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-7.0.2-x64.exe"
$expectedSha256 = "5ad54ca3def786f8f4212552e54cc6d8d61329e2d24a1cfee0571d42c2684ff1"
$downloadDirectory = Join-Path $repositoryRoot "artifacts/tools/downloads"
$downloadPath = Join-Path $downloadDirectory "innosetup-7.0.2-x64.exe"
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $resolvedInstallDirectory -Force | Out-Null

if (-not (Test-Path $downloadPath -PathType Leaf))
{
    Invoke-WebRequest -Uri $downloadUrl -OutFile $downloadPath
}

$actualSha256 = (Get-FileHash $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256)
{
    throw "Inno Setup download SHA-256 mismatch: $actualSha256"
}
$signature = Get-AuthenticodeSignature $downloadPath
if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notlike "*Pyrsys B.V.*")
{
    throw "Pinned Inno Setup download does not have the expected valid Pyrsys B.V. Authenticode signature."
}

$process = Start-Process -FilePath $downloadPath -ArgumentList @(
    "/VERYSILENT",
    "/SUPPRESSMSGBOXES",
    "/NORESTART",
    "/CURRENTUSER",
    "/DIR=$resolvedInstallDirectory"
) -Wait -PassThru
if ($process.ExitCode -ne 0 -or -not (Test-Path $compiler -PathType Leaf))
{
    throw "Pinned Inno Setup 7.0.2 installation failed with exit code $($process.ExitCode)."
}

Write-Output $compiler
