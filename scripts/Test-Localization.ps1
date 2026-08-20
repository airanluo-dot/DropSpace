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

    [xml]$resourceDocument = Get-Content -Path $Path -Raw
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
        Where-Object { $_.Extension -in ".cs", ".xaml" }
)
$hardcodedChinese = @(
    foreach ($file in $sourceFiles)
    {
        $matches = Select-String -Path $file.FullName -Pattern "[\p{IsCJKUnifiedIdeographs}\p{IsCJKCompatibilityIdeographs}]" -AllMatches
        foreach ($match in $matches)
        {
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
    $content = Get-Content -Path $file.FullName -Raw
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

$xamlUids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $sourceFiles | Where-Object Extension -eq ".xaml")
{
    $content = Get-Content -Path $file.FullName -Raw
    foreach ($match in [regex]::Matches($content, 'x:Uid="(?<uid>[^"]+)"'))
    {
        [void]$xamlUids.Add($match.Groups["uid"].Value)
    }
}

$uidsWithoutResources = @(
    $xamlUids |
        Where-Object {
            $prefix = "$($_)."
            -not @($englishNames | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) }).Count
        } |
        Sort-Object
)
if ($uidsWithoutResources.Count -gt 0)
{
    throw "XAML localization identifiers have no English resource entries: $($uidsWithoutResources -join ', ')"
}

$projectFile = Join-Path $appRoot "DropSpace.App.csproj"
if ((Get-Content -Path $projectFile -Raw) -notmatch '<DefaultLanguage>en-US</DefaultLanguage>')
{
    throw "DropSpace.App.csproj must declare en-US as the default resource language."
}

$projectText = Get-Content -Path $projectFile -Raw
if ($projectText -notmatch 'GenerateDropSpacePortableResourceIndex' -or
    $projectText -notmatch 'BundleDropSpacePortableResourceIndex')
{
    throw "DropSpace.App must generate and bundle resources.pri for unpackaged single-file builds."
}

$portablePriScript = Join-Path $repositoryRoot "scripts/Generate-PortableResourcesPri.ps1"
if (-not (Test-Path $portablePriScript -PathType Leaf) -or
    (Get-Content -Path $portablePriScript -Raw) -notmatch 'RemoveChild\(\$packagingNode\)')
{
    throw "The portable resource index generator must remove the MakePri packaging section."
}

$packageManifest = Join-Path $appRoot "Package.appxmanifest"
$manifestText = Get-Content -Path $packageManifest -Raw
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

$mainPage = Get-Content -Path (Join-Path $appRoot "Views/MainPage.xaml") -Raw
foreach ($tag in "System", "English", "SimplifiedChinese")
{
    $expectedTag = 'Tag="' + $tag + '"'
    if ($mainPage -notmatch "LanguageCombo" -or $mainPage -notmatch [regex]::Escape($expectedTag))
    {
        throw "The display-language selector is missing the '$tag' option."
    }
}

Write-Host "Localization policy passed: $($englishNames.Count) synchronized resource keys, $($imperativeResourceKeys.Count) imperative references, $($xamlUids.Count) XAML UIDs, and no Chinese hardcoding in source."
