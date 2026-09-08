param(
    [string]$RepositoryRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
} elseif ([System.IO.Path]::IsPathRooted($RepositoryRoot)) {
    [System.IO.Path]::GetFullPath($RepositoryRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $RepositoryRoot))
}

$ignore = Get-Content (Join-Path $root ".gitignore") -Raw
foreach ($pattern in @(".env", ".env.*", "*.pem", "*.key", "*.private", "secrets.*")) {
    if (-not $ignore.Contains($pattern, [StringComparison]::Ordinal)) {
        throw ".gitignore is missing the credential exclusion '$pattern'."
    }
}

$tracked = & git -C $root ls-files
foreach ($path in $tracked) {
    if ($path -match '(?i)(^|/)(?:\.env(?:\.|$)|secrets?\.|.*\.(?:pem|key|private)$)') {
        throw "A credential-like file is tracked: $path"
    }
}

Write-Host "Secret hygiene passed: credential-like suffixes are ignored and no tracked credential-like file was found."
