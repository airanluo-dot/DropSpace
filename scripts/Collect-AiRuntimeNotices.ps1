param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$requiredNotices = @('LICENSE', 'vendor/cpp-httplib/LICENSE', 'licenses/LICENSE-jsonhpp')
foreach ($notice in $requiredNotices) {
    if (-not (Test-Path (Join-Path $source $notice) -PathType Leaf)) { throw "Missing required runtime notice: $notice" }
}
$notices = Get-ChildItem $source -Recurse -File |
    Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING)([.-].*)?$' -and $_.FullName -notmatch '[\\/]\.git[\\/]' } |
    Sort-Object FullName
$licenseText = @('Notices from the pinned llama.cpp source tree. Optional unlinked components may also be listed.')
foreach ($notice in $notices) {
    $relative = [IO.Path]::GetRelativePath($source, $notice.FullName)
    $licenseText += "===== $relative =====`n" + (Get-Content $notice.FullName -Raw)
}
$licenseText -join "`n`n" | Set-Content $OutputPath -Encoding utf8
