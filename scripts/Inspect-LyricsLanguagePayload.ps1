# Static byte verification of the bundled model, x64 native engine and license notices.
# Does not execute the App, install a package, download weights or run inference.
[CmdletBinding(DefaultParameterSetName = 'Portable')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Portable')][string]$PortablePath,
    [Parameter(Mandatory, ParameterSetName = 'Portable')][string]$AppAssemblyPath,
    [Parameter(Mandatory, ParameterSetName = 'Msix')][string]$MsixPath,
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/DropSpace.Infrastructure/Lyrics/Manifests/fasttext-lid176-bin-v1.json') -Raw | ConvertFrom-Json

if (-not ('DropSpace.Packaging.LyricsLanguageInspectorV1' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
namespace DropSpace.Packaging {
public sealed class LyricsLanguageFileV1 {
    public string path { get; set; }
    public long bytes { get; set; }
    public string sha256 { get; set; }
}
public static class LyricsLanguageInspectorV1 {
    static readonly string[] Notices = {
        "fasttext-engine-MIT.txt", "fasttext-panlingo-MIT.txt", "fasttext-lid176-CC-BY-SA-3.0.txt"
    };
    static string Normalize(string name) { return name.Replace('\\', '/'); }
    static bool Selected(string name) {
        string file = Path.GetFileName(Normalize(name));
        if (file.Equals("fasttext.dll", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string notice in Notices) if (file == notice) return true;
        return false;
    }
    static void RejectUnexpectedModel(string name) {
        if (name.EndsWith(".ftz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("lid.176.bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected second model file: " + name);
    }
    static LyricsLanguageFileV1 Hash(Stream input, string name, long length) {
        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)) {
            byte[] buffer = new byte[81920];
            for (long remaining = length; remaining > 0;) {
                int read = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (read == 0) throw new EndOfStreamException();
                digest.AppendData(buffer, 0, read); remaining -= read;
            }
            return new LyricsLanguageFileV1 { path = Normalize(name), bytes = length,
                sha256 = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() };
        }
    }
    static LyricsLanguageFileV1 InspectModel(Stream input, string resourceName) {
        using (var pe = new PEReader(input, PEStreamOptions.LeaveOpen)) {
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader == null) throw new InvalidDataException("Expected managed App assembly.");
            var metadata = pe.GetMetadataReader();
            var directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
            long sectionOffset = -1;
            foreach (var section in pe.PEHeaders.SectionHeaders)
                if (directory.RelativeVirtualAddress >= section.VirtualAddress &&
                    directory.RelativeVirtualAddress - section.VirtualAddress < section.SizeOfRawData)
                    sectionOffset = section.PointerToRawData + (long)directory.RelativeVirtualAddress - section.VirtualAddress;
            if (sectionOffset < 0 || directory.Size <= 4 || sectionOffset > input.Length - directory.Size)
                throw new InvalidDataException("Invalid managed resource directory.");
            long offset = -1; int count = 0;
            foreach (var handle in metadata.ManifestResources) {
                var resource = metadata.GetManifestResource(handle);
                string name = metadata.GetString(resource.Name);
                if (name.EndsWith(".ftz", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unused ftz resource is packaged.");
                if (name.EndsWith("lid.176.bin", StringComparison.OrdinalIgnoreCase)) {
                    count++;
                    if (name != resourceName || !resource.Implementation.IsNil || resource.Offset < 0 || resource.Offset > (long)directory.Size - 4)
                        throw new InvalidDataException("Invalid bundled language model resource.");
                    offset = resource.Offset;
                }
            }
            if (count != 1) throw new InvalidDataException("Expected one bundled lid.176.bin resource; found " + count + ".");
            input.Position = sectionOffset + offset;
            using (var reader = new BinaryReader(input, Encoding.UTF8, true)) {
                uint length = reader.ReadUInt32();
                if (length <= 0 || length > (long)directory.Size - offset - 4) throw new InvalidDataException("Language model exceeds resource bounds.");
                return Hash(input, "lid.176.bin", length);
            }
        }
    }
    static string ReadString(BinaryReader reader) {
        int length = reader.Read7BitEncodedInt();
        if (length < 0 || length > 16384) throw new InvalidDataException("Invalid bundle name length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    public static LyricsLanguageFileV1[] InspectPortable(string package, string assembly, long header, string resourceName) {
        var files = new List<LyricsLanguageFileV1>();
        LyricsLanguageFileV1 appIdentity;
        using (var app = File.OpenRead(assembly)) {
            files.Add(InspectModel(app, resourceName));
            app.Position = 0; appIdentity = Hash(app, "DropSpace.dll", app.Length);
        }
        using (var input = File.OpenRead(package))
        using (var reader = new BinaryReader(input, Encoding.UTF8, true)) {
            if (header <= 0 || header > input.Length - 24) throw new InvalidDataException("Invalid bundle header.");
            input.Position = header;
            uint major = reader.ReadUInt32(), minor = reader.ReadUInt32();
            int count = reader.ReadInt32();
            if (major < 2 || major > 6 || minor != 0 || count <= 0 || count > 16384) throw new InvalidDataException("Unsupported bundle.");
            ReadString(reader);
            for (int i = 0; i < 5; i++) reader.ReadInt64();
            var selected = new List<Tuple<string, long, long>>();
            Tuple<string, long, long> appEntry = null; int appCount = 0;
            for (int i = 0; i < count; i++) {
                long offset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = major >= 6 ? reader.ReadInt64() : 0;
                reader.ReadByte(); string name = ReadString(reader);
                long stored = compressed == 0 ? size : compressed;
                if (offset < 0 || size < 0 || compressed < 0 || offset > header || stored > header - offset) throw new InvalidDataException("Invalid bundle bounds.");
                RejectUnexpectedModel(name);
                if (name == "DropSpace.dll") {
                    if (compressed != 0) throw new InvalidDataException("Unexpected compressed App entry.");
                    appEntry = Tuple.Create(name, offset, size); appCount++;
                }
                if (Selected(name)) {
                    if (compressed != 0) throw new InvalidDataException("Unexpected compressed native/notice entry.");
                    selected.Add(Tuple.Create(name, offset, size));
                }
            }
            if (appCount != 1) throw new InvalidDataException("Expected one App bundle entry.");
            input.Position = appEntry.Item2;
            var bundledApp = Hash(input, "DropSpace.dll", appEntry.Item3);
            if (bundledApp.bytes != appIdentity.bytes || bundledApp.sha256 != appIdentity.sha256)
                throw new InvalidDataException("Inspected App assembly differs from the current bundle.");
            foreach (var entry in selected) { input.Position = entry.Item2; files.Add(Hash(input, entry.Item1, entry.Item3)); }
        }
        return files.ToArray();
    }
    public static LyricsLanguageFileV1[] InspectMsix(string package, string resourceName) {
        var files = new List<LyricsLanguageFileV1>();
        using (var input = File.OpenRead(package))
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read)) {
            ZipArchiveEntry app = null; int count = 0;
            foreach (var entry in zip.Entries) {
                string name = Normalize(entry.FullName);
                RejectUnexpectedModel(name);
                if (Path.GetFileName(name) == "DropSpace.dll") { app = entry; count++; }
                if (Selected(name)) using (var stream = entry.Open()) files.Add(Hash(stream, name, entry.Length));
            }
            if (count != 1 || app.Length > 536870912) throw new InvalidDataException("Expected one bounded App assembly.");
            using (var stream = app.Open())
            using (var buffer = new MemoryStream(checked((int)app.Length))) {
                stream.CopyTo(buffer);
                if (buffer.Length != app.Length) throw new InvalidDataException("App assembly size mismatch.");
                buffer.Position = 0; files.Add(InspectModel(buffer, resourceName));
            }
        }
        return files.ToArray();
    }
}}
'@
}

$inputPath = [IO.Path]::GetFullPath($(if ($PSCmdlet.ParameterSetName -eq 'Portable') { $PortablePath } else { $MsixPath }))
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
if ([string]::Equals($inputPath, $outputFullPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Inspection output must not replace a package.' }
$package = [ordered]@{ name=[IO.Path]::GetFileName($inputPath); bytes=(Get-Item -LiteralPath $inputPath).Length; sha256=(Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant() }
if ($PSCmdlet.ParameterSetName -eq 'Portable') {
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.0\.[0-9]+$') { throw 'The pinned .NET 10 SDK is required.' }
    $dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
    Add-Type -Path (Join-Path $dotnetRoot "sdk/$sdkVersion/Microsoft.NET.HostModel.dll")
    [long]$header = 0
    if (-not [Microsoft.NET.HostModel.Bundle.Bundler]::IsBundle($inputPath, [ref]$header)) { throw 'Portable package is not a .NET bundle.' }
    $files = @([DropSpace.Packaging.LyricsLanguageInspectorV1]::InspectPortable($inputPath, [IO.Path]::GetFullPath($AppAssemblyPath), $header, $manifest.resourceName))
} else {
    $files = @([DropSpace.Packaging.LyricsLanguageInspectorV1]::InspectMsix($inputPath, $manifest.resourceName))
}
function Require-Identity([string]$Name, [long]$Bytes, [string]$Sha256) {
    $matching = @($files | Where-Object { [IO.Path]::GetFileName($_.path) -ceq $Name })
    if ($matching.Count -ne 1 -or $matching[0].bytes -ne $Bytes -or $matching[0].sha256 -cne $Sha256) { throw "Missing, duplicated or mismatched bundled $Name." }
}
Require-Identity 'lid.176.bin' $manifest.bytes $manifest.sha256
Require-Identity $manifest.nativeFile $manifest.nativeBytes $manifest.nativeSha256
foreach ($notice in @('fasttext-engine-MIT.txt', 'fasttext-panlingo-MIT.txt', 'fasttext-lid176-CC-BY-SA-3.0.txt')) {
    $source = Join-Path $repositoryRoot "docs/licenses/$notice"
    Require-Identity $notice (Get-Item -LiteralPath $source).Length (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($files.Count -ne 5) { throw 'Unexpected language-identification payload inventory.' }
if ((Get-Item -LiteralPath $inputPath).Length -ne $package.bytes -or (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.sha256) { throw 'Package changed during inspection.' }
$report = [ordered]@{ schemaVersion=1; kind='static-language-payload'; verificationScope='package bytes only; no App execution or inference'; package=$package; files=$files; noFtz=$true; nativeRid=$manifest.nativeRid }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFullPath)) | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outputFullPath -Encoding utf8NoBOM
Write-Host "Verified bundled language model, x64 native engine and three notices in $($package.name)."
