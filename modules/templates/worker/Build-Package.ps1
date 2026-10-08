[CmdletBinding()]
param(
    [string] $Project = (Join-Path $PSScriptRoot 'DropSpace.Module.Worker.csproj'),
    [string] $Manifest = (Join-Path $PSScriptRoot 'module.template.json'),
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$projectDirectory = Split-Path -Parent $projectPath
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'artifacts' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$projectBoundary = [IO.Path]::GetFullPath($projectDirectory).TrimEnd('\') + '\'
if (-not $outputPath.StartsWith($projectBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Module output must stay within its project directory.'
}
$declaration = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json -AsHashtable
& node (Join-Path $PSScriptRoot '../../check-module-resources.mjs') --manifest $Manifest
if ($LASTEXITCODE -ne 0) { throw 'Module translation integrity/review gate failed.' }
if ($declaration.Id -notmatch '^[a-z][a-z0-9.-]{0,79}$' -or $declaration.Version -notmatch '^\d+\.\d+\.\d+$' -or
    $declaration.EntryPoint -ne 'DropSpace.Module.Worker.exe') { throw 'Invalid module identity or entry point.' }
$publishDirectory = Join-Path $outputPath 'publish'
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
# Production compilation/packaging only. This entry point never launches a worker or any tests.
& dotnet restore $projectPath --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'Module locked restore failed.' }
& dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true `
    --output $publishDirectory --no-restore --nologo -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'Module publish failed.' }
$workerPath = Join-Path $publishDirectory $declaration.EntryPoint
$worker = Get-Item -LiteralPath $workerPath
$declaration.Files = @(@{
    Path = $declaration.EntryPoint
    Bytes = $worker.Length
    Sha256 = (Get-FileHash -LiteralPath $workerPath -Algorithm SHA256).Hash.ToLowerInvariant()
})
$manifestPath = Join-Path $outputPath 'manifest.json'
$declaration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$archivePath = Join-Path $outputPath ($declaration.Id + '-' + $declaration.Version + '-win-x64.zip')
if (Test-Path -LiteralPath $archivePath) { throw 'Archive already exists. Use a fresh build output directory; do not silently overwrite a pinned package.' }
$archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
try {
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        # Ordered names and fixed metadata make ZIP assembly deterministic for identical payloads.
        foreach ($entry in @(
            @{ Name = $declaration.EntryPoint; Source = $workerPath },
            @{ Name = 'manifest.json'; Source = $manifestPath }
        ) | Sort-Object Name) {
            $zipEntry = $archive.CreateEntry($entry.Name, [IO.Compression.CompressionLevel]::Optimal)
            $zipEntry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $destination = $zipEntry.Open()
            try {
                $source = [IO.File]::OpenRead($entry.Source)
                try { $source.CopyTo($destination) } finally { $source.Dispose() }
            } finally { $destination.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $archiveStream.Dispose() }
$archiveInfo = Get-Item -LiteralPath $archivePath
$metadata = [ordered]@{
    Id = $declaration.Id
    Version = $declaration.Version
    Archive = $archiveInfo.FullName
    Bytes = $archiveInfo.Length
    Sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    WorkerBytes = $worker.Length
    WorkerSha256 = $declaration.Files[0].Sha256
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'package.json') -Encoding utf8NoBOM
$metadata | ConvertTo-Json
