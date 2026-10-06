param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $RepositoryRoot 'src/DropSpace.Infrastructure/Lyrics/Manifests/fasttext-lid176-bin-v1.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$target = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $manifest.stagingPath))
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/lyrics-language'))
if (-not $target.StartsWith($expectedRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Model staging path escapes the language asset directory.' }
function Test-Model([string]$Path) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-Item -LiteralPath $Path).Length -eq $manifest.bytes) -and
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $manifest.sha256)
}
if (Test-Model $target) { Write-Host "Verified bundled lid.176.bin ($($manifest.bytes) bytes)."; exit 0 }
New-Item -ItemType Directory -Path $expectedRoot -Force | Out-Null
$temporaryModel = $target + '.download'
try {
    Invoke-WebRequest -Uri $manifest.source -OutFile $temporaryModel
    if (-not (Test-Model $temporaryModel)) { throw 'Official lid.176.bin size or SHA-256 does not match the fixed model manifest.' }
    Move-Item -LiteralPath $temporaryModel -Destination $target -Force
    Write-Host "Staged verified official lid.176.bin ($($manifest.bytes) bytes)."
} finally {
    if (Test-Path -LiteralPath $temporaryModel) { Remove-Item -LiteralPath $temporaryModel -Force }
}
