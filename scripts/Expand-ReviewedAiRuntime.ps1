# Offline byte-safe extraction only. This helper does not grant semantic approval.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ContractPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$archive = [IO.Path]::GetFullPath($ArchivePath)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Reviewed runtime destination must be new.' }
& node (Join-Path $PSScriptRoot 'ai-runtime-publication.mjs') verify-contract $ContractPath
if ($LASTEXITCODE -ne 0) { throw 'Runtime artifact contract is invalid.' }
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $contract.archiveSha256) {
    throw 'Reviewed runtime archive SHA256 mismatch.'
}
# Check every archive entry before extraction. The already-validated contract
# supplies canonical paths; ZIP contents cannot choose paths or add dependencies.
$expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $contract.files) {
    $expected.Add($file.path, $file)
    $parts = $file.path.Split('/')
    for ($index = 1; $index -lt $parts.Length; $index++) {
        $directories.Add(($parts[0..($index - 1)] -join '/') + '/') | Out-Null
    }
}
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if (-not $seen.Add($entry.FullName)) { throw 'Duplicate runtime archive entry.' }
        $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
        if ($unixType -eq 0xA000) { throw 'Runtime archive links are prohibited.' }
        if ($entry.FullName.EndsWith('/')) {
            if (-not $directories.Contains($entry.FullName) -or $entry.Length -ne 0) { throw 'Unexpected runtime archive directory.' }
        } elseif (-not $expected.ContainsKey($entry.FullName) -or $entry.Length -ne $expected[$entry.FullName].bytes) {
            throw 'Unexpected runtime archive file or byte count.'
        }
    }
    foreach ($file in $contract.files) {
        if (-not $seen.Contains($file.path)) { throw 'Reviewed runtime archive is missing a required file.' }
    }
} finally { $zip.Dispose() }
New-Item (Split-Path $output -Parent) -ItemType Directory -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $output)
& node (Join-Path $PSScriptRoot 'ai-runtime-publication.mjs') verify-runtime-directory $output $ContractPath
if ($LASTEXITCODE -ne 0) { throw 'Extracted runtime file inventory mismatch.' }
