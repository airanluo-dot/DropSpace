# Reads the managed resource table directly. Target assemblies are never loaded or executed.
[CmdletBinding(DefaultParameterSetName = 'Assembly')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Assembly')][string]$AssemblyPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Msix')][string]$MsixPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('DropSpace.Packaging.AiRuntimePayloadInspectorV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace DropSpace.Packaging
{
    public sealed class AiRuntimePayloadFileV1
    {
        public string path { get; set; }
        public string sha256 { get; set; }
        public long bytes { get; set; }
    }

    public static class AiRuntimePayloadInspectorV1
    {
        private const string Prefix = "DropSpace.AiLyricsRuntime.";

        public static AiRuntimePayloadFileV1[] InspectAssembly(string path)
        {
            using (var stream = File.OpenRead(path))
                return Inspect(stream);
        }

        public static AiRuntimePayloadFileV1[] InspectMsix(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ZipArchiveEntry candidate = null;
                int count = 0;
                foreach (var entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    name = name.Substring(name.LastIndexOf('/') + 1);
                    if (string.Equals(name, "DropSpace.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate = entry;
                        count++;
                    }
                }
                if (count != 1)
                    throw new InvalidDataException("MSIX must contain exactly one DropSpace.dll entry; found " + count + ".");

                // ZIP entry streams are not seekable. Buffer only the selected assembly,
                // without ever interpreting an archive name as a filesystem destination.
                if (candidate.Length > int.MaxValue)
                    throw new InvalidDataException("DropSpace.dll exceeds the PE reader's supported size.");
                using (var entryStream = candidate.Open())
                using (var buffer = new MemoryStream((int)candidate.Length))
                {
                    byte[] chunk = new byte[81920];
                    int read;
                    while ((read = entryStream.Read(chunk, 0, chunk.Length)) != 0)
                    {
                        if (buffer.Length + read > candidate.Length)
                            throw new InvalidDataException("DropSpace.dll exceeds its declared ZIP entry size.");
                        buffer.Write(chunk, 0, read);
                    }
                    if (buffer.Length != candidate.Length)
                        throw new InvalidDataException("DropSpace.dll does not match its declared ZIP entry size.");
                    buffer.Position = 0;
                    return Inspect(buffer);
                }
            }
        }

        private static string ResourcePath(string name)
        {
            string path = name.Substring(Prefix.Length);
            if (path.Length == 0 || path.Length > 240 || path[0] == '/')
                throw new InvalidDataException("Invalid runtime resource path: " + name);
            foreach (string segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." ||
                    segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid runtime resource path: " + name);
                foreach (char value in segment)
                {
                    if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                        (value >= '0' && value <= '9') || value == '_' || value == '.' || value == '-'))
                        throw new InvalidDataException("Invalid runtime resource path: " + name);
                }
                string device = segment.Split('.')[0].ToUpperInvariant();
                if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" ||
                    (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                        device[3] >= '1' && device[3] <= '9'))
                    throw new InvalidDataException("Invalid runtime resource path: " + name);
            }
            return path;
        }

        private static AiRuntimePayloadFileV1[] Inspect(Stream stream)
        {
            using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
            {
                if (!pe.HasMetadata || pe.PEHeaders.CorHeader == null)
                    throw new InvalidDataException("Input is not a managed assembly.");
                var metadata = pe.GetMetadataReader();
                if (!metadata.IsAssembly)
                    throw new InvalidDataException("Input is not a managed assembly.");
                var directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
                var files = new List<AiRuntimePayloadFileV1>();
                ImmutableArray<byte> contents = default;
                // The package runs on Windows, where differently-cased paths alias.
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var handle in metadata.ManifestResources)
                {
                    var resource = metadata.GetManifestResource(handle);
                    string name = metadata.GetString(resource.Name);
                    if (!name.StartsWith(Prefix, StringComparison.Ordinal))
                        continue;
                    string path = ResourcePath(name);
                    if (!paths.Add(path))
                        throw new InvalidDataException("Duplicate runtime resource path: " + path);
                    if (!resource.Implementation.IsNil)
                        throw new InvalidDataException("Linked runtime resources are not allowed: " + name);
                    if (directory.RelativeVirtualAddress <= 0 || directory.Size < 4 ||
                        resource.Offset < 0 || resource.Offset > (long)directory.Size - 4)
                        throw new InvalidDataException("Runtime resource offset is outside the managed resource directory: " + name);

                    // GetContent validates the directory against the PE's backed section.
                    // All offsets and lengths are checked before converting to int.
                    if (contents.IsDefault)
                        contents = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size);
                    int offset = checked((int)resource.Offset);
                    uint length = (uint)contents[offset] | ((uint)contents[offset + 1] << 8) |
                        ((uint)contents[offset + 2] << 16) | ((uint)contents[offset + 3] << 24);
                    if ((long)length > (long)directory.Size - offset - 4)
                        throw new InvalidDataException("Runtime resource length is outside the managed resource directory: " + name);
                    int start = checked(offset + 4);
                    byte[] digest = SHA256.HashData(contents.AsSpan(start, checked((int)length)));
                    files.Add(new AiRuntimePayloadFileV1
                    {
                        path = path,
                        sha256 = Convert.ToHexString(digest).ToLowerInvariant(),
                        bytes = length
                    });
                }
                files.Sort((left, right) => StringComparer.Ordinal.Compare(left.path, right.path));
                return files.ToArray();
            }
        }
    }
}
'@
}

$inputPath = if ($PSCmdlet.ParameterSetName -eq 'Msix') { $MsixPath } else { $AssemblyPath }
$inputFullPath = [IO.Path]::GetFullPath($inputPath)
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
if ([string]::Equals($inputFullPath, $outputFullPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must not overwrite the input assembly or MSIX.'
}
$package = if ($PSCmdlet.ParameterSetName -eq 'Msix') {
    [ordered]@{
        name = [IO.Path]::GetFileName($inputFullPath)
        sha256 = (Get-FileHash -LiteralPath $inputFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $inputFullPath).Length
    }
} else { $null }
$files = if ($PSCmdlet.ParameterSetName -eq 'Msix') {
    [DropSpace.Packaging.AiRuntimePayloadInspectorV1]::InspectMsix($inputFullPath)
} else {
    [DropSpace.Packaging.AiRuntimePayloadInspectorV1]::InspectAssembly($inputFullPath)
}
$report = [ordered]@{ schemaVersion = 1; files = @($files) }
if ($null -ne $package) {
    if ((Get-Item -LiteralPath $inputFullPath).Length -ne $package.bytes -or
        (Get-FileHash -LiteralPath $inputFullPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.sha256) {
        throw 'MSIX changed while its runtime payload was being inspected.'
    }
    $report.package = $package
}
$json = $report | ConvertTo-Json -Depth 5
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFullPath)) | Out-Null
# Publish only a complete report. A rejected input never overwrites an existing report.
$temporaryOutput = $outputFullPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllText($temporaryOutput, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryOutput, $outputFullPath, $true)
} finally {
    if ([IO.File]::Exists($temporaryOutput)) { [IO.File]::Delete($temporaryOutput) }
}
Write-Verbose "Inspected $(@($files).Count) embedded AI runtime resources in $inputFullPath."
