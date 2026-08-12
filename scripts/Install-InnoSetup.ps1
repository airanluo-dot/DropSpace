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
$partialDownloadPath = "$downloadPath.download"
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $resolvedInstallDirectory -Force | Out-Null

if (Test-Path $downloadPath -PathType Leaf)
{
    $cachedSha256 = (Get-FileHash $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($cachedSha256 -ne $expectedSha256)
    {
        Write-Warning "Discarding an incomplete or invalid cached Inno Setup download."
        Remove-Item $downloadPath -Force
    }
}

if (-not (Test-Path $downloadPath -PathType Leaf))
{
    $maximumAttempts = 3
    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++)
    {
        try
        {
            Remove-Item $partialDownloadPath -Force -ErrorAction SilentlyContinue
            Write-Host "Downloading pinned Inno Setup 7.0.2 (attempt $attempt of $maximumAttempts)..."
            Invoke-WebRequest -Uri $downloadUrl -OutFile $partialDownloadPath

            $downloadSha256 = (Get-FileHash $partialDownloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($downloadSha256 -ne $expectedSha256)
            {
                throw "Inno Setup download SHA-256 mismatch: $downloadSha256"
            }

            Move-Item $partialDownloadPath $downloadPath -Force
            break
        }
        catch
        {
            Remove-Item $partialDownloadPath -Force -ErrorAction SilentlyContinue
            if ($attempt -eq $maximumAttempts)
            {
                throw "Failed to download the pinned Inno Setup package after $maximumAttempts attempts: $($_.Exception.Message)"
            }

            $retryDelaySeconds = [Math]::Pow(2, $attempt)
            Write-Warning "Inno Setup download attempt $attempt failed: $($_.Exception.Message). Retrying in $retryDelaySeconds seconds."
            Start-Sleep -Seconds $retryDelaySeconds
        }
    }
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
