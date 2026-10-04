param([string]$OutputDirectory = 'artifacts/selection-diagnostic-inputs')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not $env:GH_TOKEN) { throw 'Cloud Windows with existing read-only Actions token required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (-not $output.StartsWith((Join-Path $root 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path $output)) { throw 'Fresh repository artifacts destination required.' }
New-Item $output -ItemType Directory | Out-Null
$headers = @{ Authorization="Bearer $env:GH_TOKEN"; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
$api = 'https://api.github.com/repos/airanluo-dot/DropSpace/actions'
$run = Invoke-RestMethod "$api/runs/37235696457/attempts/1" -Headers $headers
$run | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $output 'run.json') -Encoding utf8
$inputs = @(
    @{ name='runtime'; id=11316610580; bytes=30701589; hash='344c7aa9016e2f317b6fb157d8bca2e6103c1b1be1ce88fb450816f46589733b' },
    @{ name='evidence'; id=11316152198; bytes=6720; hash='8152aa0e06c0ea7101c06a97fcf2e2e0d57ceba20cc4f4ac5048d2a0c2eaecbd' }
)
foreach ($item in $inputs) {
    $artifact = Invoke-RestMethod "$api/artifacts/$($item.id)" -Headers $headers
    $artifact | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $output "$($item.name)-artifact.json") -Encoding utf8
}
node (Join-Path $PSScriptRoot 'selection-diagnostic-inputs.mjs') metadata $output
if ($LASTEXITCODE -ne 0) { throw 'Pinned diagnostic provenance rejected.' }
foreach ($item in $inputs) {
    $zip = Join-Path $output "$($item.name).zip"
    Invoke-WebRequest "$api/artifacts/$($item.id)/zip" -Headers $headers -OutFile $zip
    if ((Get-Item $zip).Length -ne $item.bytes -or (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $item.hash) { throw 'Pinned diagnostic archive SHA/size mismatch.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.StartsWith('/') -or $entry.FullName.Contains('\') -or $entry.FullName.Contains(':') -or $entry.FullName.Split('/') -contains '..' -or
                -not $seen.Add($entry.FullName) -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe diagnostic archive path/link.' }
        }
    } finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $output $item.name))
}
node (Join-Path $PSScriptRoot 'selection-diagnostic-inputs.mjs') payload $output
if ($LASTEXITCODE -ne 0) { throw 'Pinned diagnostic payload rejected.' }
