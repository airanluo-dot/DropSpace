param(
    [Parameter(Mandatory = $true)][string]$MetadataDirectory,
    [Parameter(Mandatory = $true)][string]$InformationalVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$version = $InformationalVersion.Split('+')
if ($version.Count -ne 2 -or $version[1] -cnotmatch '^[0-9a-f]{40}$') {
    throw 'CUDA-enabled builds require the exact App version and source commit.'
}
$descriptor = Get-Content -LiteralPath (Join-Path $MetadataDirectory 'cuda-runtime-download.json') -Raw | ConvertFrom-Json
$manifest = Get-Item -LiteralPath (Join-Path $MetadataDirectory 'cuda-runtime-manifest.json')
$tag = 'v' + $version[0]
$asset = 'DropSpace-CUDA-win-x64-' + $tag + '.zip'
if ($descriptor.appRelease.tag -cne $tag -or $descriptor.appRelease.sourceCommit -cne $version[1] -or
    $descriptor.download.name -cne $asset -or
    $descriptor.download.url -cne ('https://github.com/airanluo-dot/DropSpace/releases/download/' + $tag + '/' + $asset) -or
    $descriptor.manifest.bytes -ne $manifest.Length -or
    $descriptor.manifest.sha256 -cne (Get-FileHash -LiteralPath $manifest.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw 'CUDA metadata does not match this App build. Stage metadata for the current tag and exact source commit; never reuse another release descriptor.'
}
Write-Host "CUDA metadata bound to $InformationalVersion."
