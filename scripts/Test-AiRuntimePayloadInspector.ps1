[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$inspector = Join-Path $PSScriptRoot 'Inspect-AiRuntimePayload.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('DropSpace-runtime-payload-' + [Guid]::NewGuid().ToString('N'))
$prefix = 'DropSpace.AiLyricsRuntime.'
$passed = 0

# Construct tiny, real ECMA-335 assemblies without a compiler, restore, native
# runtime build, or model download. These assemblies are parsed, never loaded.
if (-not ('DropSpace.Packaging.RuntimePayloadFixtureV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DropSpace.Packaging
{
    public static class RuntimePayloadFixtureV1
    {
        public static void WriteAssembly(string path, string[] names, byte[][] payloads, string fault)
        {
            var metadata = new MetadataBuilder();
            metadata.AddModule(0, metadata.GetOrAddString("DropSpace.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
            metadata.AddAssembly(metadata.GetOrAddString("DropSpace"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            var resources = new BlobBuilder();
            for (int i = 0; i < names.Length; i++)
            {
                uint offset = (uint)resources.Count;
                resources.WriteUInt32(fault == "length" ? uint.MaxValue : (uint)payloads[i].Length);
                resources.WriteBytes(payloads[i]);
                EntityHandle implementation = default;
                if (fault == "linked")
                    implementation = metadata.AddAssemblyReference(metadata.GetOrAddString("external"), new Version(1, 0, 0, 0), default, default, 0, default);
                metadata.AddManifestResource(ManifestResourceAttributes.Private, metadata.GetOrAddString(names[i]), implementation,
                    fault == "offset" ? uint.MaxValue : offset);
            }
            var image = new ManagedPEBuilder(
                new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
                new MetadataRootBuilder(metadata), new BlobBuilder(), managedResources: resources, flags: CorFlags.ILOnly);
            var blob = new BlobBuilder();
            image.Serialize(blob);
            using (var stream = File.Create(path)) blob.WriteContentTo(stream);
        }
    }
}
'@
}

function New-FixtureAssembly {
    param([string]$Name, [string[]]$Names, [byte[][]]$Payloads, [string]$Fault = '')
    $path = Join-Path $fixture "$Name.dll"
    [DropSpace.Packaging.RuntimePayloadFixtureV1]::WriteAssembly($path, $Names, $Payloads, $Fault)
    return $path
}

function New-FixtureArchive {
    param([string]$Name, [string[]]$EntryNames, [string]$Assembly)
    $path = Join-Path $fixture "$Name.msix"
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $EntryNames) {
            $entry = $archive.CreateEntry($entryName)
            $stream = $entry.Open()
            try { $stream.Write([IO.File]::ReadAllBytes($Assembly)) } finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
    return $path
}

function Read-Inspection {
    param([string]$Path, [switch]$Msix)
    $output = Join-Path $fixture 'result.json'
    if ($Msix) { & $inspector -MsixPath $Path -OutputPath $output }
    else { & $inspector -AssemblyPath $Path -OutputPath $output }
    return Get-Content $output -Raw | ConvertFrom-Json
}

function Assert-Rejected {
    param([string]$Name, [string]$Path, [string]$Message, [switch]$Msix)
    $output = Join-Path $fixture 'rejected.json'
    [IO.File]::WriteAllText($output, 'previous report')
    $rejected = $false
    try {
        if ($Msix) { & $inspector -MsixPath $Path -OutputPath $output }
        else { & $inspector -AssemblyPath $Path -OutputPath $output }
    } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw "$Name failed for the wrong reason: $($_.Exception.Message)" }
        $rejected = $true
    }
    if (-not $rejected) { throw "$Name should have been rejected." }
    if ([IO.File]::ReadAllText($output) -cne 'previous report') { throw "$Name overwrote a report on failure." }
    $script:passed++
}

function Read-TestScriptAst {
    param([string]$Name)
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $Name), [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "$Name has parser errors: $errors" }
    if (@($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'RuntimeInspectionOutput' }).Count -ne 1) {
        throw "$Name must expose RuntimeInspectionOutput."
    }
    return $ast
}

function Get-TestIdentityAssignment {
    param($Ast, [string]$Variable)
    $matches = @($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -ceq ('$' + $Variable) -and $node.Right.Extent.Text -like '*[[]ordered[]]*'
    }, $true))
    if ($matches.Count -ne 1) { throw "Expected exactly one package identity assignment for $Variable." }
    return [scriptblock]::Create($matches[0].Extent.Text)
}

function Get-TestConditional {
    param($Ast, [string]$Contains)
    $matches = @($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.Contains($Contains)
    }, $true) | Sort-Object { $_.Extent.Text.Length })
    if ($matches.Count -eq 0) { throw "Expected a conditional containing: $Contains" }
    return [scriptblock]::Create($matches[0].Extent.Text)
}

try {
    New-Item $fixture -ItemType Directory | Out-Null
    [string[]]$names = @('runtime-manifest.json', 'llama-completion.exe', 'llama-completion-avx2.exe', 'llama-tokenize.exe', 'LICENSE-llama.cpp')
    [byte[][]]$payloads = @(
        [Text.Encoding]::UTF8.GetBytes('{"schemaVersion":1,"fixture":true}'),
        [byte[]](0, 1, 2, 13, 10, 128, 255),
        [byte[]](0, 0, 255, 254),
        [byte[]](1, 2, 3),
        [Text.Encoding]::UTF8.GetBytes("fixture notice`r`nUnicode: 中文`n")
    )
    $valid = New-FixtureAssembly -Name valid -Names @($names | ForEach-Object { $prefix + $_ }) -Payloads $payloads
    $result = Read-Inspection $valid
    if ($result.schemaVersion -ne 1 -or @($result.files).Count -ne $names.Count) { throw 'Valid payload inventory is incomplete.' }
    for ($i = 0; $i -lt $names.Count; $i++) {
        $entry = @($result.files | Where-Object path -CEQ $names[$i])
        $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payloads[$i])).ToLowerInvariant()
        if ($entry.Count -ne 1 -or $entry[0].sha256 -cne $expectedHash -or $entry[0].bytes -ne $payloads[$i].Length) {
            throw "Byte-exact payload comparison failed: $($names[$i])"
        }
    }
    $passed++

    $msix = New-FixtureArchive -Name valid -EntryNames @('App/DropSpace.dll', '../ignored-resource.txt', 'Other.dll') -Assembly $valid
    $packaged = Read-Inspection $msix -Msix
    if (($packaged.files | ConvertTo-Json -Depth 5 -Compress) -cne ($result.files | ConvertTo-Json -Depth 5 -Compress) -or
        $packaged.package.name -cne 'valid.msix' -or $packaged.package.bytes -ne (Get-Item $msix).Length -or
        $packaged.package.sha256 -cne (Get-FileHash $msix -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'MSIX inventory or identity differs.' }
    if (Test-Path (Join-Path $fixture '../ignored-resource.txt')) { throw 'ZIP paths were extracted.' }
    $passed++

    $future = New-FixtureAssembly -Name future -Names @(($prefix + 'future/nested.bin'), 'Other.Namespace.Resource') -Payloads @([byte[]](7), [byte[]](8))
    $futureResult = Read-Inspection $future
    if (@($futureResult.files).Count -ne 1 -or $futureResult.files[0].path -cne 'future/nested.bin') { throw 'Prefix selection or canonical path preservation failed.' }
    $passed++

    $missing = New-FixtureAssembly -Name missing -Names @($prefix + 'runtime-manifest.json') -Payloads @($payloads[0])
    if (@((Read-Inspection $missing).files).Count -ne 1) { throw 'Missing resources must remain visible to the inventory validator.' }
    $empty = New-FixtureAssembly -Name empty -Names @('Other.Resource') -Payloads @([byte[]](1))
    if (@((Read-Inspection $empty).files).Count -ne 0) { throw 'No-runtime assembly must produce an empty inventory.' }
    $passed++

    $different = New-FixtureAssembly -Name different -Names @($prefix + 'runtime-manifest.json') -Payloads @([Text.Encoding]::UTF8.GetBytes('{"fixture":false}'))
    if ((Read-Inspection $different).files[0].sha256 -ceq ($result.files | Where-Object path -CEQ 'runtime-manifest.json').sha256) { throw 'Different resource bytes were not detected.' }
    $zero = New-FixtureAssembly -Name zero -Names @($prefix + 'zero.bin') -Payloads @(,[byte[]]@())
    if ((Read-Inspection $zero).files[0].bytes -ne 0) { throw 'Zero-length resources must remain byte-exact.' }
    $passed++

    foreach ($fault in @('linked', 'offset', 'length')) {
        $invalid = New-FixtureAssembly -Name $fault -Names @($prefix + 'runtime-manifest.json') -Payloads @($payloads[0]) -Fault $fault
        $message = switch ($fault) { linked { 'Linked runtime resources' } offset { 'offset is outside' } length { 'length is outside' } }
        Assert-Rejected -Name $fault -Path $invalid -Message $message
    }
    foreach ($pair in @(
        @('duplicate', 'same.bin', 'same.bin'),
        @('case-alias', 'same.bin', 'SAME.BIN')
    )) {
        $duplicate = New-FixtureAssembly -Name $pair[0] -Names @(($prefix + $pair[1]), ($prefix + $pair[2])) -Payloads @([byte[]](1), [byte[]](2))
        Assert-Rejected -Name $pair[0] -Path $duplicate -Message 'Duplicate runtime resource path'
    }
    foreach ($badPath in @('', '../escape', '/rooted', 'C:\rooted', 'nested\file.bin', 'nested//empty', 'nested/./dot',
        'trailing.', 'trailing ', 'a:b', 'has space', '中文.bin', 'CON', 'con.txt', 'aux/file', 'COM1.bin', 'lpt9', ('a' * 241))) {
        $bad = New-FixtureAssembly -Name bad-path -Names @($prefix + $badPath) -Payloads @([byte[]](1))
        Assert-Rejected -Name "invalid path '$badPath'" -Path $bad -Message 'Invalid runtime resource path'
    }
    foreach ($case in @(
        @('missing-assembly', 'Other.dll'),
        @('duplicate-assembly', 'App/DropSpace.dll', 'Other/DropSpace.dll'),
        @('duplicate-zip-entry', 'DropSpace.dll', 'DropSpace.dll'),
        @('case-assembly-alias', 'DropSpace.dll', 'App/DROPSPACE.DLL'),
        @('separator-assembly-alias', 'DropSpace.dll', 'App\DropSpace.dll')
    )) {
        $archive = New-FixtureArchive -Name $case[0] -EntryNames $case[1..($case.Count - 1)] -Assembly $valid
        Assert-Rejected -Name $case[0] -Path $archive -Msix -Message 'exactly one DropSpace.dll'
    }
    $invalidPe = Join-Path $fixture 'invalid.dll'
    [IO.File]::WriteAllBytes($invalidPe, [byte[]](1, 2, 3))
    Assert-Rejected -Name 'invalid PE' -Path $invalidPe -Message ''
    $truncated = Join-Path $fixture 'truncated.dll'
    $validBytes = [IO.File]::ReadAllBytes($valid)
    [IO.File]::WriteAllBytes($truncated, $validBytes[0..127])
    Assert-Rejected -Name 'truncated PE' -Path $truncated -Message ''

    $inputHash = (Get-FileHash $valid -Algorithm SHA256).Hash
    $rejected = $false
    try { & $inspector -AssemblyPath $valid -OutputPath $valid } catch { $rejected = $_.Exception.Message -like '*must not overwrite*' }
    if (-not $rejected -or (Get-FileHash $valid -Algorithm SHA256).Hash -cne $inputHash) { throw 'Input/output collision was not rejected safely.' }
    $passed++

    # Exercise the actual portable/installer identity and report statements with
    # harmless files. AST extraction avoids running any Windows launch, registry,
    # install, or uninstall code on this cross-platform fixture runner.
    $portableAst = Read-TestScriptAst 'Test-PortableSmoke.ps1'
    $installerAst = Read-TestScriptAst 'Test-InstallerLifecycle.ps1'
    $resolvedExecutable = Join-Path $fixture 'DropSpace.exe'
    [IO.File]::Copy($valid, $resolvedExecutable)
    $runtimeInspectionPath = Join-Path $fixture 'portable-report.json'
    . (Get-TestIdentityAssignment $portableAst 'runtimePackage')
    $bundleExtractRoot = Join-Path $fixture 'fresh-extraction'
    New-Item (Join-Path $bundleExtractRoot 'bundle') -ItemType Directory -Force | Out-Null
    $extractedAssembly = Join-Path $bundleExtractRoot 'bundle/DropSpace.dll'
    [IO.File]::Copy($valid, $extractedAssembly)
    # This outer conditional includes the exact-one check plus inspector invocation.
    $inspectionStatements = @($portableAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text.Contains("'Inspect-AiRuntimePayload.ps1'")
    }, $true))
    if ($inspectionStatements.Count -ne 1) { throw 'Portable smoke must inspect exactly one extracted assembly block.' }
    # A parsed fragment has no script-file location; retain the real script root.
    $observePortable = [scriptblock]::Create($inspectionStatements[0].Extent.Text.Replace('$PSScriptRoot', ("'" + $PSScriptRoot.Replace("'", "''") + "'")))
    . $observePortable
    if (($runtimeInspection.files | ConvertTo-Json -Depth 5 -Compress) -cne ($result.files | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'Fresh portable observation differs from assembly resources.'
    }
    $reportStatements = @($portableAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text.Contains('$runtimeInspection.package = $runtimePackage')
    }, $true))
    if ($reportStatements.Count -ne 1) { throw 'Portable smoke report writer must be unique.' }
    $writePortableReport = [scriptblock]::Create($reportStatements[0].Extent.Text)
    . $writePortableReport
    $portableReport = Get-Content $runtimeInspectionPath -Raw | ConvertFrom-Json
    if ($portableReport.package.name -cne 'DropSpace.exe' -or $portableReport.package.sha256 -cne $runtimePackage.sha256 -or
        $portableReport.package.bytes -ne $runtimePackage.bytes -or $portableReport.schemaVersion -ne 1) { throw 'Portable package binding report is invalid.' }
    $passed++

    [IO.File]::WriteAllBytes($resolvedExecutable, [byte[]](3, 2, 1))
    $rejected = $false
    try { . $writePortableReport } catch { $rejected = $_.Exception.Message -like '*Portable executable changed*' }
    if (-not $rejected) { throw 'Portable observation accepted a changed executable.' }
    [IO.File]::Copy($valid, $resolvedExecutable, $true)
    $passed++

    [IO.File]::Copy($valid, (Join-Path $bundleExtractRoot 'DropSpace.dll'))
    $rejected = $false
    try { . $observePortable } catch { $rejected = $_.Exception.Message -like '*exactly one DropSpace.dll*' }
    if (-not $rejected) { throw 'Portable observation accepted ambiguous extracted assemblies.' }
    Remove-Item (Join-Path $bundleExtractRoot 'DropSpace.dll'), $extractedAssembly
    $rejected = $false
    try { . $observePortable } catch { $rejected = $_.Exception.Message -like '*exactly one DropSpace.dll*' }
    if (-not $rejected) { throw 'Portable observation accepted missing extracted assemblies.' }
    $passed++

    $currentInstallerPath = Join-Path $fixture 'DropSpaceSetup.exe'
    [IO.File]::WriteAllBytes($currentInstallerPath, [byte[]](9, 8, 7))
    $portablePath = $resolvedExecutable
    $installedExe = Join-Path $fixture 'installed-DropSpace.exe'
    [IO.File]::Copy($valid, $installedExe)
    . (Get-TestIdentityAssignment $installerAst 'installerIdentity')
    . (Get-TestIdentityAssignment $installerAst 'portableIdentity')
    $captureInstalled = Get-TestIdentityAssignment $installerAst 'installedPortableIdentity'
    . $captureInstalled
    $checkInstalled = Get-TestConditional $installerAst 'Installed DropSpace.exe bytes do not match'
    . $checkInstalled
    $checkInstaller = Get-TestConditional $installerAst 'Current installer changed during lifecycle'
    $checkPortable = Get-TestConditional $installerAst 'Release portable executable changed during lifecycle'
    . $checkInstaller
    . $checkPortable
    $writeInstallerReport = Get-TestConditional $installerAst "kind = 'installer-payload'"
    $runtimeInspectionPath = Join-Path $fixture 'installer-report.json'
    . $writeInstallerReport
    $installerReport = Get-Content $runtimeInspectionPath -Raw | ConvertFrom-Json
    if ($installerReport.schemaVersion -ne 1 -or $installerReport.kind -cne 'installer-payload' -or
        $installerReport.package.name -cne 'DropSpaceSetup.exe' -or $installerReport.package.sha256 -cne $installerIdentity.sha256 -or
        $installerReport.package.bytes -ne $installerIdentity.bytes -or $installerReport.installedPortable.name -cne 'DropSpace.exe' -or
        $installerReport.installedPortable.sha256 -cne $portableIdentity.sha256 -or $installerReport.installedPortable.bytes -ne $portableIdentity.bytes) {
        throw 'Installer package/installed portable binding report is invalid.'
    }
    $passed++

    [IO.File]::WriteAllBytes($installedExe, [byte[]](1, 2, 3))
    . $captureInstalled
    $rejected = $false
    try { . $checkInstalled } catch { $rejected = $_.Exception.Message -like '*Installed DropSpace.exe bytes do not match*' }
    if (-not $rejected) { throw 'Lifecycle accepted an installed executable different from the portable.' }
    $passed++

    # Same-size mutations ensure hash checks, not only length checks, fail closed.
    [IO.File]::WriteAllBytes($currentInstallerPath, [byte[]](9, 8, 6))
    $rejected = $false
    try { . $checkInstaller } catch { $rejected = $_.Exception.Message -like '*Current installer changed*' }
    if (-not $rejected) { throw 'Lifecycle accepted a changed installer.' }
    $mutatedPortable = [IO.File]::ReadAllBytes($portablePath)
    $mutatedPortable[-1] = $mutatedPortable[-1] -bxor 1
    [IO.File]::WriteAllBytes($portablePath, $mutatedPortable)
    $rejected = $false
    try { . $checkPortable } catch { $rejected = $_.Exception.Message -like '*Release portable executable changed*' }
    if (-not $rejected) { throw 'Lifecycle accepted a changed portable.' }
    $passed++
    Write-Host "Runtime payload inspector regressions passed: $passed checks (assembly/MSIX, byte-exact hashes, safe paths, malformed-resource rejection, portable/installer binding statements)."
} finally {
    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
}
