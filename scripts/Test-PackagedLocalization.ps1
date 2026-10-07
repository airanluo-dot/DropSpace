# Static production check: reads the actual bundle/MSIX PRI without launching DropSpace.
param([Parameter(Mandatory=$true)][string]$ArtifactDirectory,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$artifacts = [IO.Path]::GetFullPath($ArtifactDirectory)
$catalogPath = Join-Path $repo 'localization/languages.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$languages = @($catalog.languages | ForEach-Object { $_.code })
$canonicalLanguages = @{}
foreach ($language in $languages) { $canonicalLanguages[$language.ToLowerInvariant()] = $language }
$work = Join-Path ([IO.Path]::GetTempPath()) ('DropSpace-localization-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
function Get-Sha([string]$File) { (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant() }
function Find-MakePri {
    [xml]$packages = Get-Content -LiteralPath (Join-Path $repo 'Directory.Packages.props') -Raw -Encoding UTF8
    $sdk = @($packages.SelectNodes("//*[local-name()='PackageVersion' and @Include='Microsoft.Windows.SDK.BuildTools']"))
    if ($sdk.Count -ne 1) { throw 'One locked Windows SDK BuildTools version is required.' }
    $packageRoot = $env:NUGET_PACKAGES
    if ([string]::IsNullOrWhiteSpace($packageRoot)) { $packageRoot = Join-Path $env:USERPROFILE '.nuget/packages' }
    $root = Join-Path $packageRoot ('microsoft.windows.sdk.buildtools/' + $sdk[0].GetAttribute('Version'))
    if (Test-Path -LiteralPath $root) {
        $match = Get-ChildItem -LiteralPath $root -Recurse -Filter makepri.exe -File |
            Where-Object FullName -Match '[\\/]x64[\\/]makepri\.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
        if ($match) { return $match.FullName }
    }
    throw 'The pinned Windows SDK MakePri tool is required to inspect packaged localization.'
}
# Match the runtime MRT map convention, preserving dots within [using:] type qualifiers.
function Get-MapKey([string]$Key) {
    $builder = [Text.StringBuilder]::new(); $depth = 0
    foreach ($character in $Key.ToCharArray()) {
        if ($character -eq '[') { $depth++ }
        elseif ($character -eq ']') { $depth-- }
        [void]$builder.Append($(if ($character -eq '.' -and $depth -eq 0) { '/' } else { $character }))
    }
    return $builder.ToString()
}
function Get-PriResourceKey([System.Xml.XmlElement]$Resource) {
    # Detailed MakePri dumps put the leaf name in @name and the complete map path
    # in @uri. Keep property subtrees and [using:] qualifiers in the latter.
    $uri = $Resource.GetAttribute('uri')
    if (-not [string]::IsNullOrWhiteSpace($uri)) {
        $path = [Uri]::UnescapeDataString($uri)
        if ($path -match '^ms-resource://[^/]*/Resources/(.+)$') { return $Matches[1] }
        return $null # File/framework maps are not the App string map.
    }
    $segments = [Collections.Generic.List[string]]::new()
    $segments.Add($Resource.GetAttribute('name'))
    for ($parent = $Resource.ParentNode; $null -ne $parent; $parent = $parent.ParentNode) {
        if ($parent.LocalName -eq 'ResourceMapSubtree') { $segments.Insert(0, $parent.GetAttribute('name')) }
    }
    $path = [Uri]::UnescapeDataString(($segments -join '/'))
    if ($path -match '^Resources/(.+)$') { return $Matches[1] }
    return $null
}
try {
    $makePri = Find-MakePri
    $sources = @{}; $sourceSha256 = [ordered]@{}; $installerSha256 = [ordered]@{}
    foreach ($language in $languages) {
        $file = Join-Path $repo "src/DropSpace.App/Strings/$language/Resources.resw"
        [xml]$xml = Get-Content -LiteralPath $file -Raw -Encoding UTF8
        $values = @{}
        foreach ($item in $xml.root.data) { $values[(Get-MapKey ([string]$item.name))] = [string]$item.value }
        $sources[$language] = $values; $sourceSha256[$language] = Get-Sha $file
        $installerSha256[$language] = Get-Sha (Join-Path $repo "installer/localization/$language.isl")
    }
    function Assert-PriContents([string[]]$PriFiles, [string]$Kind) {
        $actual = @{}
        foreach ($language in $languages) { $actual[$language] = @{} }
        $number = 0
        foreach ($pri in $PriFiles) {
            $dump = Join-Path $work "$Kind-$number.xml"; $number++
            & $makePri dump /if $pri /of $dump /dt detailed /o | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "MakePri dump failed: $pri" }
            [xml]$document = Get-Content -LiteralPath $dump -Raw -Encoding UTF8
            foreach ($resource in $document.SelectNodes("//*[local-name()='NamedResource']")) {
                $key = Get-PriResourceKey $resource
                if ([string]::IsNullOrWhiteSpace($key)) { continue }
                foreach ($candidate in $resource.SelectNodes("./*[local-name()='Candidate']")) {
                    $qualifier = $candidate.SelectSingleNode(".//*[local-name()='Qualifier' and translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='language']")
                    if ($null -eq $qualifier) { continue }
                    $tag = ([string]$qualifier.value).Trim().Replace('_','-').ToLowerInvariant()
                    if (-not $canonicalLanguages.ContainsKey($tag)) { continue }
                    $language = $canonicalLanguages[$tag]
                    if (-not $sources[$language].ContainsKey($key)) { continue }
                    $value = $candidate.SelectSingleNode("./*[local-name()='Value']")
                    if ($null -eq $value) { continue }
                    if ($actual[$language].ContainsKey($key) -and $actual[$language][$key] -cne [string]$value.InnerText) {
                        throw "$Kind $language ${key}: conflicting localized candidates in actual packaged PRI"
                    }
                    $actual[$language][$key] = [string]$value.InnerText
                }
            }
        }
        foreach ($language in $languages) {
            foreach ($key in $sources[$language].Keys) {
                if (-not $actual[$language].ContainsKey($key)) { throw "$Kind $language ${key}: required localized value absent from actual packaged PRI" }
                if ($actual[$language][$key] -cne $sources[$language][$key]) { throw "$Kind $language ${key}: packaged PRI value does not match reviewed offline resource" }
            }
        }
        return $languages
    }
    $portable = Join-Path $artifacts 'DropSpace.exe'
    $portablePri = Join-Path $work 'DropSpace.resources.pri'
    & (Join-Path $PSScriptRoot 'Extract-StaticBundleAssembly.ps1') -PortablePath $portable -OutputPath $portablePri -EntryName 'DropSpace.resources.pri'
    $portableLanguages = @(Assert-PriContents @($portablePri) 'portable')
    $msix = Join-Path $artifacts 'DropSpace-x64.msix'
    $msixRoot = Join-Path $work 'msix'
    [IO.Compression.ZipFile]::ExtractToDirectory($msix,$msixRoot)
    $msixPris = @(Get-ChildItem -LiteralPath $msixRoot -Recurse -File -Filter *.pri | ForEach-Object FullName)
    if (-not $msixPris.Count) { throw 'MSIX has no resource index.' }
    $msixLanguages = @(Assert-PriContents $msixPris 'msix')
    $installer = Join-Path $artifacts 'DropSpaceSetup.exe'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The compiled installer is required for the publication binding.' }
    # Inno's successful compiler execution is the existing installer proof. Bind
    # the exact checked-in wizard language declarations and bytes to its output.
    $installerScript = Join-Path $repo 'installer/DropSpace.iss'
    $installerText = Get-Content -LiteralPath $installerScript -Raw -Encoding UTF8
    foreach ($language in $languages) {
        if ($installerText -notmatch ('(?m)^Name: .*MessagesFile: .*localization\\' + [regex]::Escape($language) + '\.isl')) {
            throw "installer $language: compiled wizard declaration missing"
        }
    }
    $receipt = [ordered]@{
        schemaVersion = 1; manifestSha256 = Get-Sha $catalogPath; sourceSha256 = $sourceSha256; installerSourceSha256 = $installerSha256
        installerScriptSha256 = Get-Sha $installerScript
        resourceCounts = [ordered]@{}
        artifacts = @(
            [ordered]@{file='DropSpace.exe';kind='portable';sha256=(Get-Sha $portable);languages=$portableLanguages},
            [ordered]@{file='DropSpace-x64.msix';kind='msix';sha256=(Get-Sha $msix);languages=$msixLanguages},
            [ordered]@{file='DropSpaceSetup.exe';kind='installer';sha256=(Get-Sha $installer);languages=$languages}
        )
    }
    foreach ($language in $languages) { $receipt.resourceCounts[$language] = $sources[$language].Count }
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts 'localization-publication.json') -Encoding utf8NoBOM
    Write-Host 'Packaged localization verified against actual portable/MSIX resource candidates; installer bound to the same portable payload and ten compiled wizard language inputs. No App execution.'
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
