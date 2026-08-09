param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Register", "Unregister")]
    [string]$Action,

    [string]$PackagePath = "",

    [string]$ExternalLocation = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Action -eq "Unregister")
{
    Get-AppxPackage -Name "AiranLuo.DropSpace.Identity" |
        Remove-AppxPackage
    exit 0
}

if (-not (Test-Path $PackagePath -PathType Leaf))
{
    throw "The signed DropSpace identity package is missing."
}
if (-not (Test-Path $ExternalLocation -PathType Container))
{
    throw "The DropSpace external application location is missing."
}

$signature = Get-AuthenticodeSignature $PackagePath
if ($signature.Status -ne "Valid")
{
    throw "The DropSpace identity package is not trusted: $($signature.Status)."
}

$parameters = @{
    Path = $PackagePath
    ExternalLocation = $ExternalLocation
    ForceApplicationShutdown = $true
}
Add-AppxPackage @parameters
