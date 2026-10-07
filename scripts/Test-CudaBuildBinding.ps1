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
$runtimeId = 'llama-cpp-v0.5.0-cuda13-win-x64-v1'
$componentTag = 'cuda-' + $runtimeId
$asset = 'DropSpace-CUDA-' + $runtimeId + '.zip'
$inner = Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json
$hashProvider = [System.Security.Cryptography.SHA256]::Create()
$manifestStream = [System.IO.File]::OpenRead($manifest.FullName)
try { $manifestHash = [BitConverter]::ToString($hashProvider.ComputeHash($manifestStream)).Replace('-', '').ToLowerInvariant() }
finally { $manifestStream.Dispose(); $hashProvider.Dispose() }
if ($descriptor.schemaVersion -ne 2 -or $descriptor.repository -cne 'airanluo-dot/DropSpace' -or
    $descriptor.runtimeId -cne $runtimeId -or $descriptor.componentRelease.tag -cne $componentTag -or
    $descriptor.componentSourceCommit -cne '806f3e3e40c11a6e7d3d50648a9de8708b16b4ac' -or
    $descriptor.backend -cne 'cuda' -or $descriptor.platform -cne 'win-x64' -or
    $descriptor.protocol -ne 1 -or $descriptor.profile -cne 'hy-q8-plain-resident-v1' -or
    $descriptor.engineSourceCommit -cne '7fe450e19305b828c199d602c23a8337aaa1f03b' -or
    $descriptor.workerSourceSha256 -cne $inner.workerSourceSha256 -or
    $inner.runtimeId -cne $runtimeId -or $inner.protocol -ne 1 -or
    $inner.profile -cne $descriptor.profile -or $inner.sourceCommit -cne $descriptor.engineSourceCommit -or
    $descriptor.download.bytes -le 0 -or $descriptor.download.bytes -gt 1073741824 -or
    $descriptor.download.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    $descriptor.appRelease.tag -cne $tag -or $descriptor.appRelease.sourceCommit -cne $version[1] -or
    $descriptor.download.name -cne $asset -or
    $descriptor.download.url -cne ('https://github.com/airanluo-dot/DropSpace/releases/download/' + $componentTag + '/' + $asset) -or
    $descriptor.manifest.bytes -ne $manifest.Length -or
    $descriptor.manifest.sha256 -cne $manifestHash) {
    throw 'CUDA metadata does not match this App build. Stage metadata for the current tag and exact source commit; never reuse another release descriptor.'
}
Write-Host "CUDA component $componentTag bound to App $InformationalVersion."
