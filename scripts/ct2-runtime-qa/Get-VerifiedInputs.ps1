[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Input directory must be new.' }
$lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') | ConvertFrom-Json
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'provenance.json') | ConvertFrom-Json
if ($lock.packages.Count -ne 11 -or $source.packages.Count -ne 11) { throw 'Unexpected dependency count.' }
New-Item -ItemType Directory -Path $Output | Out-Null
$wheels = New-Item -ItemType Directory -Path (Join-Path $Output 'wheelhouse')
$licenses = New-Item -ItemType Directory -Path (Join-Path $Output 'licenses')
function Get-VerifiedFile($Url, $Destination, $Bytes, $Sha256) {
    $uri = [Uri]$Url
    if ($uri.Scheme -cne 'https' -or $uri.Host -notin @('files.pythonhosted.org', 'www.python.org', 'raw.githubusercontent.com') -or $uri.UserInfo) { throw 'Unreviewed source.' }
    Invoke-WebRequest -Uri $uri -OutFile $Destination -MaximumRedirection 3
    if ((Get-Item -LiteralPath $Destination).Length -ne $Bytes -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash.ToLowerInvariant() -cne $Sha256) { throw 'Input size/hash mismatch.' }
}
foreach ($package in $lock.packages) {
    $matches = @($source.packages | Where-Object { $_.name -ceq $package.name -and $_.version -ceq $package.version })
    if ($matches.Count -ne 1 -or [IO.Path]::GetFileName(([Uri]$matches[0].url).AbsolutePath) -cne $package.file) { throw 'Provenance mismatch.' }
    Get-VerifiedFile $matches[0].url (Join-Path $wheels.FullName $package.file) $package.bytes $package.sha256
}
foreach ($license in $source.supplementaryLicenses) {
    if ($license.file -notmatch '^[a-z0-9-]+-LICENSE\.txt$') { throw 'Invalid license filename.' }
    Get-VerifiedFile $license.url (Join-Path $licenses.FullName $license.file) $license.bytes $license.sha256
}
$installer = $source.pythonInstaller
Get-VerifiedFile $installer.url (Join-Path $Output $installer.file) $installer.bytes $installer.sha256
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json'), (Join-Path $PSScriptRoot 'provenance.json') -Destination $Output
