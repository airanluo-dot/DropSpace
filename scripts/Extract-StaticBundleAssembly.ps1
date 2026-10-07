# Reads the .NET 10 bundle manifest and copies its single managed App assembly.
# The target EXE is never launched. SDK tool code locates the official bundle marker.
param([Parameter(Mandatory=$true)][string]$PortablePath,[Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateSet('DropSpace.dll','DropSpace.resources.pri')][string]$EntryName = 'DropSpace.dll')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$sdkVersion=(& dotnet --version).Trim()
if($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.0\.[0-9]+$'){throw 'The locked .NET 10 SDK is required.'}
$dotnetRoot=Split-Path (Get-Command dotnet).Source -Parent
$hostModel=Join-Path $dotnetRoot ('sdk\'+$sdkVersion+'\Microsoft.NET.HostModel.dll')
Add-Type -Path $hostModel
[long]$header=0
if(![Microsoft.NET.HostModel.Bundle.Bundler]::IsBundle([IO.Path]::GetFullPath($PortablePath),[ref]$header)){throw 'Portable EXE is not a single-file bundle.'}
if(-not ('DropSpace.Packaging.StaticBundleAssemblyV1' -as [type])){
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
namespace DropSpace.Packaging {
public static class StaticBundleAssemblyV1 {
  static string ReadString(BinaryReader reader) {
    int length=reader.Read7BitEncodedInt();
    if(length<0 || length>16384) throw new InvalidDataException("Bundle string exceeds its bound.");
    byte[] bytes=reader.ReadBytes(length);
    if(bytes.Length!=length) throw new EndOfStreamException();
    return new UTF8Encoding(false,true).GetString(bytes);
  }
  public static void Extract(string input,string output,long header,string entryName) {
    using(var stream=File.OpenRead(input))
    using(var reader=new BinaryReader(stream,new UTF8Encoding(false,true),true)) {
      if(header<=0 || header>stream.Length-24) throw new InvalidDataException("Invalid bundle header offset.");
      stream.Position=header;
      uint major=reader.ReadUInt32(),minor=reader.ReadUInt32();
      int count=reader.ReadInt32();
      if(major<2 || major>6 || minor!=0 || count<=0 || count>16384) throw new InvalidDataException("Unsupported bundle manifest.");
      ReadString(reader);
      for(int i=0;i<5;i++) reader.ReadInt64();
      long selectedOffset=0,selectedSize=0;
      int selectedCount=0;
      for(int i=0;i<count;i++) {
        long offset=reader.ReadInt64(),size=reader.ReadInt64();
        long compressed=major>=6?reader.ReadInt64():0;
        byte type=reader.ReadByte();
        string name=ReadString(reader);
        long stored=compressed==0?size:compressed;
        if(offset<0 || size<0 || compressed<0 || offset>header || stored>header-offset) throw new InvalidDataException("Bundle entry is outside its payload.");
        if(name==entryName) {
          if((entryName=="DropSpace.dll" && type!=1) || compressed!=0 || size<=0 || size>536870912) throw new InvalidDataException("Unsupported selected bundle entry.");
          selectedCount++;selectedOffset=offset;selectedSize=size;
        }
      }
      if(selectedCount!=1) throw new InvalidDataException("Expected exactly one selected App bundle entry: "+entryName);
      stream.Position=selectedOffset;
      using(var destination=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
        byte[] buffer=new byte[81920];
        for(long remaining=selectedSize;remaining>0;) {
          int read=stream.Read(buffer,0,(int)Math.Min(remaining,buffer.Length));
          if(read==0) throw new EndOfStreamException();
          destination.Write(buffer,0,read);remaining-=read;
        }
      }
    }
  }
}}
'@
}
[DropSpace.Packaging.StaticBundleAssemblyV1]::Extract([IO.Path]::GetFullPath($PortablePath),[IO.Path]::GetFullPath($OutputPath),$header,$EntryName)
