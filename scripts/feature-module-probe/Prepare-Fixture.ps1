[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SampleZip,
    [Parameter(Mandatory)] [ValidateSet('traversal', 'hash', 'required-capability', 'incompatible-update')] [string] $Mode
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$samplePath = (Resolve-Path -LiteralPath $SampleZip).Path
$outputDirectory = Join-Path $PSScriptRoot ('artifacts/fixtures/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sourceZip = [IO.Compression.ZipFile]::OpenRead($samplePath)
try {
    $reader = [IO.StreamReader]::new($sourceZip.GetEntry('manifest.json').Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
    if ($manifest.Id -ne 'dropspace.sample' -or $manifest.Version -ne '1.0.0') { throw 'Expected the actual independently built sample.' }
    $manifest.Version = '1.0.1'
    switch ($Mode) {
        'traversal' { $manifest.Files += @{ Path = '../outside.txt'; Bytes = 5; Sha256 = '0000000000000000000000000000000000000000000000000000000000000000' } }
        'hash' { $manifest.Files[0].Sha256 = '0000000000000000000000000000000000000000000000000000000000000000' }
        'required-capability' { $manifest.RequiredCapabilities += @{ Id = 'unknown.required'; Version = 99 } }
        'incompatible-update' { $manifest.Data = @{ Version = 2; MinimumReadableVersion = 2; MaximumReadableVersion = 2 } }
    }
    $candidatePath = Join-Path $outputDirectory 'dropspace.sample-1.0.1-win-x64.zip'
    $output = [IO.File]::Open($candidatePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
    try {
        $candidate = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($source in $sourceZip.Entries | Where-Object FullName -ne 'manifest.json') {
                $entry = $candidate.CreateEntry($source.FullName)
                $destination = $entry.Open()
                try { $input = $source.Open(); try { $input.CopyTo($destination) } finally { $input.Dispose() } } finally { $destination.Dispose() }
            }
            $entry = $candidate.CreateEntry('manifest.json')
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write(($manifest | ConvertTo-Json -Depth 20)) } finally { $writer.Dispose() }
            if ($Mode -eq 'traversal') {
                $entry = $candidate.CreateEntry('../outside.txt')
                $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
                try { $writer.Write('probe') } finally { $writer.Dispose() }
            }
        } finally { $candidate.Dispose() }
    } finally { $output.Dispose() }
} finally { $sourceZip.Dispose() }
$packages = @()
foreach ($file in @($samplePath, $candidatePath)) {
    $info = Get-Item -LiteralPath $file
    $version = if ($file -eq $samplePath) { '1.0.0' } else { '1.0.1' }
    $package = [ordered]@{
        id = 'dropspace.sample'; version = $version
        url = ('https://github.com/airanluo-dot/DropSpace/releases/download/dlc-dropspace.sample-' + $version + '/dropspace.sample-' + $version + '-win-x64.zip')
        bytes = $info.Length
        sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $packages += $package
}
$previousDescriptor = Join-Path $outputDirectory 'previous-package-descriptor.json'
$packages[0] | ConvertTo-Json | Set-Content -LiteralPath $previousDescriptor -Encoding utf8NoBOM
$catalogFile = Join-Path $outputDirectory 'fixture-catalog.json'
@{ schemaVersion = 1; packages = @($packages[1]) } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $catalogFile -Encoding utf8NoBOM
[ordered]@{
    Mode = $Mode; Candidate = $candidatePath; Catalog = $catalogFile
    PreviousDescriptor = $previousDescriptor
    Provenance = 'Deliberately malformed local fixture with honest archive SHA and official URL-shaped descriptors embedded only in this probe; no public release or publisher claim.'
} | ConvertTo-Json
