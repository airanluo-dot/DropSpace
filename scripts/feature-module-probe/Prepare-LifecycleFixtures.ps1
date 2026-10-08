$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$destination = Join-Path $PSScriptRoot 'artifacts/lifecycle-fixtures'
if (Test-Path -LiteralPath $destination) { throw 'Use a clean disposable checkout; fixtures are never overwritten.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$workerProject = Join-Path $PSScriptRoot 'fixture-worker/FixtureWorker.csproj'
dotnet publish $workerProject -c Release -o "$destination/publish"
if ($LASTEXITCODE -ne 0) { throw 'Fixture worker compilation failed.' }
$worker = Join-Path $destination 'publish/FixtureWorker.exe'
$workerBytes = (Get-Item -LiteralPath $worker).Length
$workerHash = (Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash.ToLowerInvariant()
$packages = @()
foreach ($identity in @(@{Id='probe.a';Version='1.0.0';Dependency='probe.b'},
    @{Id='probe.b';Version='1.0.0';Dependency=$null}, @{Id='probe.b';Version='2.0.0';Dependency=$null},
    @{Id='probe.c';Version='1.0.0';Dependency='probe.d'}, @{Id='probe.d';Version='1.0.0';Dependency='probe.c'})) {
    $resources = @{}
    foreach ($language in @('en-US','zh-CN','zh-TW','ja-JP','ko-KR','de-DE','fr-FR','es-ES','pt-BR','ru-RU')) {
        $resources[$language] = @{name='Lifecycle test fixture'}
    }
    $dependencies = @()
    if ($identity.Dependency) { $dependencies += @{Id=$identity.Dependency;MinimumVersion='1.0.0';MaximumVersionExclusive='2.0.0'} }
    $manifest = @{
        Id=$identity.Id;Version=$identity.Version;EntryPoint='FixtureWorker.exe';Name=@{Key='name'}
        Resources=$resources;Dependencies=$dependencies
        Files=@(@{Path='FixtureWorker.exe';Bytes=$workerBytes;Sha256=$workerHash})
    }
    $name = "$($identity.Id)-$($identity.Version)-win-x64.zip"
    $archivePath = Join-Path $destination $name
    $file = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
    try {
        $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $payload = $zip.CreateEntry('FixtureWorker.exe').Open()
            try {
                $input = [IO.File]::OpenRead($worker)
                try { $input.CopyTo($payload) } finally { $input.Dispose() }
            } finally { $payload.Dispose() }
            $writer = [IO.StreamWriter]::new($zip.CreateEntry('manifest.json').Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write(($manifest | ConvertTo-Json -Depth 15)) } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
    } finally { $file.Dispose() }
    $packages += @{
        id=$identity.Id;version=$identity.Version
        url="https://github.com/airanluo-dot/DropSpace/releases/download/dlc-$($identity.Id)-$($identity.Version)/$name"
        bytes=(Get-Item -LiteralPath $archivePath).Length
        sha256=(Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
@{schemaVersion=1;packages=$packages} | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $destination 'catalog.json') -Encoding utf8NoBOM
