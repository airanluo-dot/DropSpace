#!/usr/bin/env python3
"""Frozen C# crypto interoperability; only fixed public test keys are exported."""
import argparse,json,os,pathlib,subprocess
root=pathlib.Path(__file__).resolve().parents[2]; baseline='1cd4620dd58a67b0963b73487743137813b8864a'
p=argparse.ArgumentParser(); p.add_argument('--dotnet',required=True); p.add_argument('--verify-native'); args=p.parse_args()
scratch=root/'macos/build/ShareCryptoBaselineFixture'; scratch.mkdir(parents=True,exist_ok=True)
for name,path in [('Models','src/DropSpace.Core/Transfer/TransferModels.cs'),('Crypto','src/DropSpace.Infrastructure/Sharing/ShareCryptoService.cs')]:
 (scratch/(name+'.cs')).write_bytes(subprocess.check_output(['git','show',baseline+':'+path],cwd=root))
(scratch/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
(scratch/'Program.cs').write_text(r'''using System.Text; using System.Text.Json; using System.Security.Cryptography;
using DropSpace.Infrastructure.Sharing;
var crypto=new ShareCryptoService(); var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
var key=Enumerable.Range(0,32).Select(i=>(byte)i).ToArray();
var share=Guid.Parse("a1b2c3d4-e5f6-4789-abcd-0123456789ef"); var file=Guid.Parse("fedcba98-7654-4321-abcd-ef0123456789");
var prefix=Convert.FromHexString("0011223344556677"); var nonce=Convert.FromHexString("000102030405060708090a0b");
var plain=Encoding.UTF8.GetBytes("DropSpace 中文 😀\npublic crypto fixture");
var items=new[]{new EncryptedShareManifestItem(file,"中文 😀 & file.txt","text/plain",plain.Length,1,Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant(),prefix)};
if(args.Length==1){
 using var doc=JsonDocument.Parse(File.ReadAllText(args[0])); var root=doc.RootElement;
 var manifest=ShareCryptoService.UnpackManifestWire(Convert.FromBase64String(root.GetProperty("manifestWire").GetString()!));
 var decoded=crypto.DecryptManifest(key,share,manifest.Nonce,manifest.Ciphertext,manifest.Tag);
 if(decoded.Count!=1 || decoded[0].FileId!=file || decoded[0].DisplayName!=items[0].DisplayName || !decoded[0].NoncePrefix.SequenceEqual(prefix)) throw new Exception("Native manifest mismatch");
 var wire=Convert.FromBase64String(root.GetProperty("chunkWire").GetString()!);
 var restored=crypto.DecryptChunk(key,share,file,0,plain.Length,wire[..^16],wire[^16..],prefix);
 if(!restored.SequenceEqual(plain)) throw new Exception("Native chunk mismatch");
 Console.WriteLine("Frozen C# decrypted native manifest and chunk; public fixture bytes match.");return;
}
var chunks=new List<object>();
foreach(var index in new[]{0,1,256,int.MaxValue}){
 var chunk=crypto.EncryptChunk(key,share,file,index,plain,prefix);
 chunks.Add(new{index,plain,wire=ShareCryptoService.PackChunkWire(chunk.Ciphertext,chunk.Tag)});
}
var encrypted=crypto.EncryptManifest(key,share,items,nonce);
File.WriteAllText("results.json",JsonSerializer.Serialize(new{key,share,file,prefix,nonce,items,chunks,manifestWire=ShareCryptoService.PackManifestWire(encrypted.Nonce,encrypted.Ciphertext,encrypted.Tag),guidWire=ShareCryptoService.GuidWireBytes(share),fragment=ShareCryptoService.ToUrlFragment(key)},options));
''')
env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_GENERATE_ASPNET_CERTIFICATE='false',DOTNET_CLI_HOME=str(root/'macos/build/dotnet-cli'))
command=[str(pathlib.Path(args.dotnet).resolve()),'run','--project','Fixture.csproj']
if args.verify_native: command+=['--',str(pathlib.Path(args.verify_native).resolve())]
subprocess.run(command,cwd=scratch,env=env,check=True)
if not args.verify_native:
 result=json.loads((scratch/'results.json').read_text());result['baseline']=baseline
 target=root/'macos/Tests/DropSpaceInfrastructureTests/Fixtures/share-crypto-baseline.json'
 target.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n');print(target)
