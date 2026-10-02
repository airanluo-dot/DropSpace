param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$appRoot = Join-Path $repositoryRoot "src/DropSpace.App"
$englishResources = Join-Path $appRoot "Strings/en-US/Resources.resw"
$simplifiedChineseResources = Join-Path $appRoot "Strings/zh-CN/Resources.resw"

function Get-ResourceNames([string]$Path)
{
    if (-not (Test-Path $Path -PathType Leaf))
    {
        throw "Localized resource file is missing: $Path"
    }

    [xml]$resourceDocument = Get-Content -Path $Path -Raw -Encoding UTF8
    $names = @($resourceDocument.root.data | ForEach-Object { [string]$_.name })
    if ($names.Count -eq 0)
    {
        throw "Localized resource file has no entries: $Path"
    }

    $duplicates = @($names | Group-Object | Where-Object Count -gt 1 | Select-Object -ExpandProperty Name)
    if ($duplicates.Count -gt 0)
    {
        throw "Localized resource file has duplicate names: $Path :: $($duplicates -join ', ')"
    }

    return @($names | Sort-Object -Unique)
}

$englishNames = Get-ResourceNames $englishResources
$simplifiedChineseNames = Get-ResourceNames $simplifiedChineseResources
$resourceDifference = Compare-Object -ReferenceObject $englishNames -DifferenceObject $simplifiedChineseNames
if ($null -ne $resourceDifference)
{
    $formattedDifference = $resourceDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
    throw "English and Simplified Chinese resource keys must match exactly:`n$($formattedDifference -join [Environment]::NewLine)"
}

$englishLookup = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in $englishNames)
{
    [void]$englishLookup.Add($name)
}

$sourceFiles = @(
    Get-ChildItem -Path (Join-Path $repositoryRoot "src") -Recurse -File |
        Where-Object { $_.Extension -in ".cs", ".xaml" -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
)
# These three frozen model-facing constants are an evaluated wire protocol, not UI.
# Verify exact bytes/definitions and exempt only these lines, never the whole file.
$modelProtocolPath = 'src/DropSpace.Core/Lyrics/PlainHyLyricsProtocol.cs'
$modelProtocolLines = @(
    'public const string EnglishTarget = "英语";',
    'public const string ChineseTarget = "简体中文";',
    'public const string Template = "将以下文本翻译为{0}，注意只需要输出翻译后的结果，不要额外解释：\n{1}";'
)
$protocolLines = @(Get-Content -LiteralPath (Join-Path $repositoryRoot $modelProtocolPath) -Encoding UTF8 | ForEach-Object { $_.Trim() })
foreach ($expectedLine in $modelProtocolLines) {
    if (@($protocolLines | Where-Object { $_ -ceq $expectedLine }).Count -ne 1) {
        throw 'The frozen Hy model-facing protocol changed; preserve evaluated prompt bytes and review protocol changes explicitly.'
    }
}
# Host-side lyric language/credit vocabulary is input classification data, not UI.
# Bind exact data declarations in this one Core file; additions or edits require
# a reviewed policy change, and copying the same bytes into UI code is forbidden.
$languagePolicyPath = 'src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs'
$languagePolicyLineHashes = @(
    'a1491838c3fff5a19891656102552fa8442759624761e8bf279329f1dd743940'
    '123ff1374ed5569a5aad2a9bf867ce8aa74fdba026d586a5a4d567c339cfb718'
    'f9ce14ff105eb6d848a4ecfcbaf0374aa58aff312bb3df5167a771b141587486'
    '0ec39eaf32fc5744d3d1e7a2c703249b5dd43e8e256ac525229f611daa429ab7'
    '3f39a25c5777280fb90ab5e74e13433ca927649cae0cd4964adb7c32a607f10f'
    '3542193c36ce62a424b45adac6726055b5c579c4c28250d33d58f6ee18e58db4'
)
function Get-LanguagePolicyLineHash([string]$Line) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Line.Trim())
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
$actualLanguageHashes = @(Get-Content -LiteralPath (Join-Path $repositoryRoot $languagePolicyPath) -Encoding UTF8 |
    ForEach-Object { Get-LanguagePolicyLineHash $_ })
foreach ($expectedHash in $languagePolicyLineHashes) {
    if (@($actualLanguageHashes | Where-Object { $_ -ceq $expectedHash }).Count -ne 1) {
        throw 'The host lyric language vocabulary changed; review its exact data declarations and localization policy together.'
    }
}
$hardcodedChinese = @(
    foreach ($file in $sourceFiles)
    {
        $matches = Select-String -Path $file.FullName -Pattern "[\p{IsCJKUnifiedIdeographs}\p{IsCJKCompatibilityIdeographs}]" -AllMatches -Encoding UTF8
        foreach ($match in $matches)
        {
            $relativePath = [IO.Path]::GetRelativePath($repositoryRoot, $file.FullName).Replace('\', '/')
            if ($relativePath -ceq $modelProtocolPath -and $modelProtocolLines -ccontains $match.Line.Trim()) { continue }
            if ($relativePath -ceq $languagePolicyPath -and $languagePolicyLineHashes -ccontains (Get-LanguagePolicyLineHash $match.Line)) { continue }
            "{0}:{1}:{2}" -f $file.FullName.Substring($repositoryRoot.Length + 1), $match.LineNumber, $match.Line.Trim()
        }
    }
)
if ($hardcodedChinese.Count -gt 0)
{
    throw "Chinese UI text must be placed in .resw resources, not source files:`n$($hardcodedChinese -join [Environment]::NewLine)"
}

$imperativeResourceKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $sourceFiles | Where-Object Extension -eq ".cs")
{
    $content = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    foreach ($match in [regex]::Matches($content, '_strings\.(?:Get|Format)\("(?<key>[^"]+)"'))
    {
        [void]$imperativeResourceKeys.Add($match.Groups["key"].Value)
    }
}

$missingImperativeKeys = @($imperativeResourceKeys | Where-Object { -not $englishLookup.Contains($_) } | Sort-Object)
if ($missingImperativeKeys.Count -gt 0)
{
    throw "Imperative localized strings are missing from Resources.resw: $($missingImperativeKeys -join ', ')"
}

$xamlResourceIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $sourceFiles | Where-Object Extension -eq ".xaml")
{
    $content = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    if ($content -match '\bx:Uid=')
    {
        $relativePath = $file.FullName.Substring($repositoryRoot.Length + 1)
        throw "Unpackaged XAML must use XamlResourceOverride.Uid instead of x:Uid: $relativePath"
    }

    foreach ($match in [regex]::Matches($content, 'services:XamlResourceOverride\.Uid="(?<uid>[^"]+)"'))
    {
        [void]$xamlResourceIds.Add($match.Groups["uid"].Value)
    }
}

$windowOverrides = @(
    @{ Uid = "MainTitleBar"; Target = "AppTitleBar"; RequireLoaded = $true; CodeBehind = Join-Path $appRoot "MainWindow.xaml.cs" },
    @{ Uid = "MainWindow"; Target = "this"; RequireLoaded = $false; CodeBehind = Join-Path $appRoot "MainWindow.xaml.cs" },
    @{ Uid = "OverlayWindow"; Target = "this"; RequireLoaded = $false; CodeBehind = Join-Path $appRoot "OverlayWindow.xaml.cs" }
)
foreach ($windowOverride in $windowOverrides)
{
    $uid = $windowOverride.Uid
    $target = $windowOverride.Target
    $codeBehindPath = $windowOverride.CodeBehind
    if (-not (Test-Path $codeBehindPath -PathType Leaf))
    {
        throw "Window localization identifier '$uid' has no code-behind override."
    }

    $codeBehind = Get-Content -Path $codeBehindPath -Raw -Encoding UTF8
    $applyPattern = 'XamlResourceOverride\.Apply\(\s*' + [regex]::Escape($target) + '\s*,\s*"' + [regex]::Escape($uid) + '"\)'
    if ($codeBehind -notmatch $applyPattern)
    {
        throw "Window localization identifier '$uid' must be applied from code-behind."
    }

    if ($windowOverride.RequireLoaded -and $codeBehind -notmatch 'AppTitleBar\.Loaded\s*\+=')
    {
        throw "MainTitleBar localization must wait for the TitleBar Loaded event."
    }

    [void]$xamlResourceIds.Add($uid)
}

$resourceIdsWithoutResources = @(
    $xamlResourceIds |
        Where-Object {
            $prefix = "$($_)."
            -not @($englishNames | Where-Object { $_.IndexOf($prefix, [System.StringComparison]::Ordinal) -eq 0 }).Count
        } |
        Sort-Object
)
if ($resourceIdsWithoutResources.Count -gt 0)
{
    throw "XAML localization identifiers have no English resource entries: $($resourceIdsWithoutResources -join ', ')"
}

$projectFile = Join-Path $appRoot "DropSpace.App.csproj"
if ((Get-Content -Path $projectFile -Raw -Encoding UTF8) -notmatch '<DefaultLanguage>en-US</DefaultLanguage>')
{
    throw "DropSpace.App.csproj must declare en-US as the default resource language."
}

$projectText = Get-Content -Path $projectFile -Raw -Encoding UTF8
if ($projectText -notmatch 'GenerateDropSpacePortableResourceIndex' -or
    $projectText -notmatch 'BundleDropSpacePortableResourceIndex' -or
    $projectText -notmatch 'DropSpace\.resources\.pri')
{
    throw "DropSpace.App must generate and explicitly bundle its non-default localization PRI for unpackaged single-file builds."
}

$portablePriScript = Join-Path $repositoryRoot "scripts/Generate-PortableResourcesPri.ps1"
if (-not (Test-Path $portablePriScript -PathType Leaf) -or
    (Get-Content -Path $portablePriScript -Raw -Encoding UTF8) -notmatch 'RemoveChild\(\$packagingNode\)' -or
    (Get-Content -Path $portablePriScript -Raw -Encoding UTF8) -notmatch 'Copy-ProjectFile' -or
    (Get-Content -Path $portablePriScript -Raw -Encoding UTF8) -notmatch '"Assets"' -or
    (Get-Content -Path $portablePriScript -Raw -Encoding UTF8) -match 'GetRelativePath')
{
    throw "The portable resource index generator must omit package identity while staging XAML and asset resources with Windows PowerShell-compatible paths."
}

$resourceLocalizerPath = Join-Path $appRoot "Services/ResourceStringLocalizer.cs"
$resourceLocalizerText = Get-Content -Path $resourceLocalizerPath -Raw -Encoding UTF8
if ($resourceLocalizerText -notmatch 'ToResourceMapPath' -or
    $resourceLocalizerText -notmatch 'bracketDepth' -or
    $resourceLocalizerText -match "key\.Replace\('\.',\s*'/'\)")
{
    throw "MRT resource lookup must preserve dots inside XAML [using:] type qualifiers for accessibility names."
}

$packageManifest = Join-Path $appRoot "Package.appxmanifest"
$manifestText = Get-Content -Path $packageManifest -Raw -Encoding UTF8
if ($manifestText -notmatch 'DisplayName="ms-resource:AppDisplayName"' -or
    $manifestText -notmatch 'Description="ms-resource:AppDescription"')
{
    throw "The package manifest must resolve its display name and description from localized resources."
}

foreach ($manifestResource in "AppDisplayName", "AppDescription")
{
    if (-not $englishLookup.Contains($manifestResource))
    {
        throw "The package manifest resource '$manifestResource' is missing from Resources.resw."
    }
}

$mainPage = Get-Content -Path (Join-Path $appRoot "Views/MainPage.xaml") -Raw -Encoding UTF8
foreach ($tag in "System", "English", "SimplifiedChinese")
{
    $expectedTag = 'Tag="' + $tag + '"'
    if ($mainPage -notmatch "LanguageCombo" -or $mainPage -notmatch [regex]::Escape($expectedTag))
    {
        throw "The display-language selector is missing the '$tag' option."
    }
}

Write-Host "Localization policy passed: $($englishNames.Count) synchronized resource keys, $($imperativeResourceKeys.Count) imperative references, $($xamlResourceIds.Count) XAML resource identifiers, with only the three frozen model protocol constants and six exact host vocabulary declarations exempted."
