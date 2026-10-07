param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot ".."),
    [ValidateSet('all','app','website')][string]$Scope = 'all'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$arguments = @((Join-Path $PSScriptRoot 'check-localization.mjs'), '--root', $repositoryRoot)
if ($Scope -ne 'all') { $arguments += @('--scope', $Scope) }
& node @arguments
if ($LASTEXITCODE -ne 0) { throw 'Localization integrity failed; publication is blocked.' }

# Compatibility fixture for the existing manually requested frozen-protocol guard suite.
# The production gate above runs this policy in the shared Node checker, once.
# This helper is not invoked by build/release, and is not a second resource scan.
function Assert-LegacyFrozenProtocolFixture {
    $sourceFiles = @(Get-ChildItem (Join-Path $repositoryRoot 'src') -Recurse -File)
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
    '5f8af9b2d2b8b4a2bee94fd38db524e1a7d5fd6c1370909ca0266ee671ca39b8'
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

$imperativeResourceKeys = $null # Historical harness boundary, not an extra scan.
}
