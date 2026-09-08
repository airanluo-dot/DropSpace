param(
    [string]$RepositoryRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
} elseif ([System.IO.Path]::IsPathRooted($RepositoryRoot)) {
    [System.IO.Path]::GetFullPath($RepositoryRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $RepositoryRoot))
}

$ignore = Get-Content (Join-Path $root ".gitignore") -Raw
foreach ($pattern in @(".env", ".env.*", "*.pem", "*.key", "*.private", "secrets.*")) {
    if (-not $ignore.Contains($pattern, [StringComparison]::Ordinal)) {
        throw ".gitignore is missing the credential exclusion '$pattern'."
    }
}

$tracked = & git -C $root ls-files
$textExtensions = @(
    ".c", ".cc", ".cpp", ".cs", ".csproj", ".css", ".fs", ".fsx", ".go", ".h", ".hpp",
    ".html", ".ini", ".js", ".json", ".jsx", ".md", ".props", ".ps1", ".psm1", ".py",
    ".resw", ".rs", ".sh", ".sln", ".svg", ".ts", ".tsx", ".txt", ".toml", ".yml", ".yaml"
)

# Keep the signatures high-confidence. Prefixes are assembled so this policy
# file cannot become a source of a credential-shaped literal itself.
$githubPrefix = "gh" + "p_"
$slackPrefix = "xox"
$awsPrefix = "A" + "KIA"
$patterns = [ordered]@{
    "github-token" = [regex]::new([regex]::Escape($githubPrefix) + "[A-Za-z0-9_]{30,}")
    "slack-token" = [regex]::new([regex]::Escape($slackPrefix) + "[baprs]-[A-Za-z0-9-]{20,}")
    "aws-access-key" = [regex]::new([regex]::Escape($awsPrefix) + "[A-Z0-9]{16}")
    "private-key" = [regex]::new("-----BEGIN [A-Z0-9 ]+ PRIVATE KEY-----")
    "jwt" = [regex]::new("eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")
    "credential-assignment" = [regex]::new("(?im)(?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|password)\s*[:=]\s*[\"']?[A-Za-z0-9/+_=.-]{24,}")
}
function Find-SecretMatches {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Signatures
    )

    foreach ($entry in $Signatures.GetEnumerator()) {
        foreach ($match in $entry.Value.Matches($Text)) {
            $line = 1 + ($Text.Substring(0, $match.Index).Split("`n").Count - 1)
            [pscustomobject]@{
                Category = $entry.Key
                Line = $line
            }
        }
    }
}

$fixtures = @(
    @{ Text = $githubPrefix + ("A" * 36); Category = "github-token" },
    @{ Text = $awsPrefix + ("B" * 16); Category = "aws-access-key" },
    @{ Text = "password = '" + ("C" * 32) + "'"; Category = "credential-assignment" }
)
foreach ($fixture in $fixtures) {
    if (@(Find-SecretMatches -Text $fixture.Text -Signatures $patterns).Category -notcontains $fixture.Category) {
        throw "The secret scanner self-test did not detect its $($fixture.Category) fixture."
    }
}

$findings = [System.Collections.Generic.List[object]]::new()
foreach ($path in $tracked) {
    if ($path -match '(?i)(^|/)(?:\.env(?:\.|$)|secrets?\.|.*\.(?:pem|key|private)$)') {
        throw "A credential-like file is tracked: $path"
    }

    $extension = [System.IO.Path]::GetExtension($path)
    if ($textExtensions -notcontains $extension.ToLowerInvariant()) {
        continue
    }

    $fullPath = Join-Path $root $path
    $fileInfo = Get-Item $fullPath -ErrorAction Stop
    if ($fileInfo.Length -gt 4MB) {
        continue
    }

    $text = [System.IO.File]::ReadAllText($fullPath, [System.Text.Encoding]::UTF8)
    foreach ($finding in @(Find-SecretMatches -Text $text -Signatures $patterns)) {
        $findings.Add([pscustomobject]@{
                Path = $path
                Category = $finding.Category
                Line = $finding.Line
            })
    }
}

if ($findings.Count -gt 0) {
    $summary = $findings | ForEach-Object { "$($_.Path):$($_.Line) [$($_.Category)]" }
    throw "Potential credential material found in tracked text: $($summary -join ', ')"
}

Write-Host "Secret hygiene passed: filename exclusions, content signatures, and scanner fixtures are clean."
