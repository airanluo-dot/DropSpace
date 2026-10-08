[CmdletBinding()]
param([string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '../templates/worker/Build-Package.ps1') `
    -Project (Join-Path $PSScriptRoot 'DropSpace.Module.Sample.csproj') `
    -Manifest (Join-Path $PSScriptRoot '../templates/worker/module.template.json') `
    -OutputDirectory $OutputDirectory
