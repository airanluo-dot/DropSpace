# Reuses the exact language asset already shipped in Beta16. Never requests a
# model source, executes the old App, or touches any CUDA archive.
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $repo 'src/DropSpace.Infrastructure/Lyrics/Manifests/fasttext-lid176-bin-v1.json') -Raw | ConvertFrom-Json
$target = Join-Path $repo 'artifacts/lyrics-language/lid.176.bin'
function Assert-Model([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -ne $manifest.bytes -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.sha256) {
        throw 'Previously bundled language model size/SHA256 mismatch.'
    }
}
if (Test-Path -LiteralPath $target) { Assert-Model $target; Write-Host 'Reused verified language asset from the exact cache.'; return }
$releaseVersion = (Get-Content (Join-Path $repo 'RELEASE_VERSION') -Raw).Trim()
$prAssetDirectory = if ($releaseVersion -ceq 'v0.3.1-beta.18') { 'artifacts/beta18-pr-validation' } else { 'artifacts/beta17-pr-validation' }
$prAsset = Join-Path $repo "$prAssetDirectory/lid.176.bin"
if (Test-Path -LiteralPath $prAsset) {
    Assert-Model $prAsset
    New-Item (Split-Path $target -Parent) -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath $prAsset -Destination $target
    Write-Host 'Reused exact language bytes retained by the successful PR producer.'
    return
}
if ($env:GITHUB_REF -ceq 'refs/heads/main') {
    throw 'Final packaging requires the verified PR language asset; no repeated old-App download fallback.'
}
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -cne 'airanluo-dot/DropSpace' -or
    $releaseVersion -cnotin @('v0.3.1-beta.17','v0.3.1-beta.18')) {
    throw 'Published asset reuse is limited to the reviewed Beta17/Beta18 producers.'
}
$work = Join-Path $env:RUNNER_TEMP ('DropSpace-beta16-language-' + [guid]::NewGuid().ToString('N'))
New-Item $work -ItemType Directory | Out-Null
try {
    $portable = Join-Path $work 'DropSpace.exe'
    Invoke-WebRequest -Uri 'https://github.com/airanluo-dot/DropSpace/releases/download/v0.3.1-beta.16/DropSpace.exe' -OutFile $portable
    if ((Get-Item $portable).Length -ne 488249448 -or
        (Get-FileHash $portable -Algorithm SHA256).Hash.ToLowerInvariant() -cne '0a9af9f7dda1fc77251ec224f84e72023fe64cb3f0e3d5caa20627db3ae61ac1') {
        throw 'Existing Beta16 portable does not match its reviewed public bytes.'
    }
    $assembly = Join-Path $work 'DropSpace.dll'
    & (Join-Path $PSScriptRoot 'Extract-StaticBundleAssembly.ps1') -PortablePath $portable -OutputPath $assembly
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
public static class DropSpaceExistingLanguageAsset {
    public static void Extract(string assembly, string destination, string resourceName, long expectedSize) {
        using (var input = File.OpenRead(assembly))
        using (var pe = new PEReader(input, PEStreamOptions.LeaveOpen)) {
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader == null) throw new InvalidDataException("Expected managed App assembly.");
            var metadata = pe.GetMetadataReader();
            var directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
            long sectionOffset = -1;
            foreach (var section in pe.PEHeaders.SectionHeaders)
                if (directory.RelativeVirtualAddress >= section.VirtualAddress && directory.RelativeVirtualAddress - section.VirtualAddress < section.SizeOfRawData)
                    sectionOffset = section.PointerToRawData + (long)directory.RelativeVirtualAddress - section.VirtualAddress;
            if (sectionOffset < 0 || directory.Size <= 4 || sectionOffset > input.Length - directory.Size) throw new InvalidDataException("Invalid resource directory.");
            long offset = -1; int count = 0;
            foreach (var handle in metadata.ManifestResources) {
                var resource = metadata.GetManifestResource(handle);
                if (metadata.GetString(resource.Name) != resourceName) continue;
                if (!resource.Implementation.IsNil || resource.Offset < 0 || resource.Offset > (long)directory.Size - 4) throw new InvalidDataException("Invalid embedded model.");
                offset = resource.Offset; count++;
            }
            if (count != 1) throw new InvalidDataException("Expected exactly one previously bundled language resource.");
            input.Position = sectionOffset + offset;
            using (var reader = new BinaryReader(input, System.Text.Encoding.UTF8, true)) {
                long size = reader.ReadUInt32();
                if (size != expectedSize || size > (long)directory.Size - offset - 4) throw new InvalidDataException("Language resource size mismatch.");
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    byte[] buffer = new byte[81920];
                    for (long remaining = size; remaining > 0;) {
                        int read = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                        if (read == 0) throw new EndOfStreamException();
                        output.Write(buffer, 0, read); remaining -= read;
                    }
                }
            }
        }
    }
}
'@
    $extracted = Join-Path $work 'lid.176.bin'
    [DropSpaceExistingLanguageAsset]::Extract($assembly, $extracted, $manifest.resourceName, $manifest.bytes)
    Assert-Model $extracted
    New-Item (Split-Path $target -Parent) -ItemType Directory -Force | Out-Null
    Move-Item -LiteralPath $extracted -Destination $target
    Write-Host 'Reused the unchanged language asset from verified Beta16 package bytes; no model source download or inference.'
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
