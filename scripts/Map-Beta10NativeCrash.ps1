param([string]$Output = 'artifacts/beta10-native-crash')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Exact native symbol lookup requires Windows DbgHelp.' }
New-Item -ItemType Directory -Force $Output | Out-Null
$root = (Resolve-Path $Output).Path
$package = Join-Path $root 'host.nupkg'
Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.host.win-x64/10.0.12/microsoft.netcore.app.host.win-x64.10.0.12.nupkg' -OutFile $package
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entry = $archive.GetEntry('runtimes/win-x64/native/singlefilehost.exe')
    if ($null -eq $entry) { throw 'Missing exact host binary.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $root 'singlefilehost.exe'), $true)
} finally { $archive.Dispose() }

# Fetch Microsoft's symbols for the existing release host. No application build,
# AI engine/model download, package publication, or user-machine access occurs.
$symbolKey = '22B27B3C55234123B45BC7CBEC2D9EE31'
$pdb = Join-Path $root 'singlefilehost.pdb'
Invoke-WebRequest "https://msdl.microsoft.com/download/symbols/singlefilehost.pdb/$symbolKey/singlefilehost.pdb" -OutFile $pdb

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class Beta10SymbolLookup
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct ModuleInfo
    {
        public uint SizeOfStruct; public ulong BaseOfImage;
        public uint ImageSize, TimeDateStamp, CheckSum, NumSyms, SymType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ModuleName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ImageName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string LoadedImageName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string LoadedPdbName;
        public uint CVSig;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 780)] public string CVData;
        public uint PdbSig; public Guid PdbSig70; public uint PdbAge;
        [MarshalAs(UnmanagedType.Bool)] public bool PdbUnmatched;
        [MarshalAs(UnmanagedType.Bool)] public bool DbgUnmatched;
        [MarshalAs(UnmanagedType.Bool)] public bool LineNumbers;
        [MarshalAs(UnmanagedType.Bool)] public bool GlobalSymbols;
        [MarshalAs(UnmanagedType.Bool)] public bool TypeInfo;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceIndexed;
        [MarshalAs(UnmanagedType.Bool)] public bool Publics;
        public uint MachineType, Reserved;
    }
    public sealed class Address
    {
        public string Rva, Symbol, Displacement, SymbolRva;
    }
    public sealed class Result
    {
        public string PdbGuid, Timestamp; public uint PdbAge; public Address[] Addresses;
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SymInitialize(IntPtr process, string path, bool invade);
    [DllImport("dbghelp.dll")] static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern ulong SymLoadModuleEx(IntPtr process, IntPtr file, string image, string module, ulong address, uint size, IntPtr data, uint flags);
    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SymGetModuleInfo64(IntPtr process, ulong address, ref ModuleInfo info);
    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
    [DllImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SymCleanup(IntPtr process);
    public static Result Lookup(string image, string directory)
    {
        var process = GetCurrentProcess();
        SymSetOptions(0x2 | 0x4 | 0x10 | 0x200 | 0x80000); // Exact symbols; no UI.
        if (!SymInitialize(process, directory, false)) throw new Win32Exception();
        try
        {
            var address = SymLoadModuleEx(process, IntPtr.Zero, image, "singlefilehost", 0x140000000, 0, IntPtr.Zero, 0);
            if (address == 0) throw new Win32Exception();
            var info = new ModuleInfo { SizeOfStruct = (uint)Marshal.SizeOf<ModuleInfo>() };
            if (!SymGetModuleInfo64(process, address, ref info)) throw new Win32Exception();
            if (info.PdbUnmatched || info.PdbSig70 != new Guid("22b27b3c-5523-4123-b45b-c7cbec2d9ee3") || info.PdbAge != 1 || info.TimeDateStamp != 0x6a89b77a)
                throw new InvalidOperationException("Host or PDB does not match Beta10 exactly.");
            var rvas = new ulong[] { 0x19c4c8, 0x5b6e20, 0x1022a0 };
            var found = new Address[rvas.Length];
            for (var i = 0; i < rvas.Length; i++)
            {
                var symbol = Marshal.AllocHGlobal(88 + 1024);
                try
                {
                    // SYMBOL_INFO's fixed native header is 88 bytes on x64;
                    // its Name starts at offset 84.
                    Marshal.Copy(new byte[88 + 1024], 0, symbol, 88 + 1024);
                    Marshal.WriteInt32(symbol, 0, 88); Marshal.WriteInt32(symbol, 80, 1024);
                    ulong displacement;
                    if (!SymFromAddr(process, address + rvas[i], out displacement, symbol)) throw new Win32Exception();
                    found[i] = new Address { Rva = "0x" + rvas[i].ToString("x"),
                        Symbol = Marshal.PtrToStringAnsi(symbol + 84, Marshal.ReadInt32(symbol, 76)),
                        SymbolRva = "0x" + ((ulong)Marshal.ReadInt64(symbol, 56) - address).ToString("x"),
                        Displacement = "0x" + displacement.ToString("x") };
                }
                finally { Marshal.FreeHGlobal(symbol); }
            }
            return new Result { PdbGuid = info.PdbSig70.ToString(), PdbAge = info.PdbAge,
                Timestamp = "0x" + info.TimeDateStamp.ToString("x"), Addresses = found };
        }
        finally { SymCleanup(process); }
    }
}
'@

$result = [Beta10SymbolLookup]::Lookup((Join-Path $root 'singlefilehost.exe'), $root)
$record = [ordered]@{
    releaseCommit = '4044baed6b1c393a815b91e1406a83e0995f1f94'
    releaseExeSha256 = 'caaf091ac30a355abf85c90be6f622dc5b26f1fe509b7ee0e913816889b840c7'
    runtime = '10.0.12'
    pdbSha256 = (Get-FileHash $pdb -Algorithm SHA256).Hash.ToLowerInvariant()
    exactHostSymbols = $result
    limitation = 'Reported address mapping identifies the runtime reporting site; a matching crash dump is still required to identify the original corrupting operation.'
}
$record | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'address-map.json') -Encoding utf8
$record | ConvertTo-Json -Depth 6 | Write-Host
