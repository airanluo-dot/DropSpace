function Assert-DropSpaceExecutableVersion
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$ReleaseInfo,
        [switch]$Installer
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Release executable is missing: $Path" }
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    $numeric = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart).$($info.FilePrivatePart)"
    $expectedFileText = if ($Installer) { $ReleaseInfo.SemanticVersion } else { $ReleaseInfo.FileVersion }
    # Inno Setup pads its string version-resource fields with trailing spaces.
    # The numeric version remains exact; normalize only documented installer text.
    $fileText = if ($Installer) { $info.FileVersion.TrimEnd() } else { $info.FileVersion }
    $productText = if ($Installer) { $info.ProductVersion.TrimEnd() } else { $info.ProductVersion }
    $productName = if ($Installer) { $info.ProductName.TrimEnd() } else { $info.ProductName }
    if ($numeric -ne $ReleaseInfo.FileVersion -or $fileText -ne $expectedFileText -or
        $productText -ne $ReleaseInfo.SemanticVersion -or $productName -ne 'DropSpace' -or
        (-not $Installer -and ($info.InternalName -ne 'DropSpace' -or $info.OriginalFilename -ne 'DropSpace.exe')))
    {
        throw "Release executable identity/version mismatch: $Path (expected $($ReleaseInfo.SemanticVersion), actual $($info.ProductVersion))."
    }
}
