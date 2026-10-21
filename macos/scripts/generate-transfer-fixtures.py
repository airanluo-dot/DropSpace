#!/usr/bin/env python3
"""Execute the immutable transfer policy; identify OS-dependent filename results."""
import argparse, json, os, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
baseline = '1cd4620dd58a67b0963b73487743137813b8864a'
parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', required=True); args = parser.parse_args()
scratch = root / 'macos/build/TransferBaselineFixture'; scratch.mkdir(parents=True, exist_ok=True)
def source(path): return subprocess.check_output(['git', 'show', baseline + ':' + path], cwd=root).decode('utf-8-sig')
policy = source('src/DropSpace.Core/Transfer/TransferPolicies.cs')
(scratch/'Policy.cs').write_text('namespace DropSpace.Core.Transfer;\n'+policy[policy.index('public static class TransferManifestPolicy'):policy.index('public static class ClipboardEnvelopePolicy')])
(scratch/'Models.cs').write_text(source('src/DropSpace.Core/Transfer/TransferModels.cs'))
(scratch/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
(scratch/'Program.cs').write_text(r'''using System.Text.Json;
using System.Text.Json.Nodes;
using DropSpace.Core.Transfer;
var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true};
var item=new TransferItemManifest(Guid.Parse("11111111-2222-3333-4444-555555555555"),TransferItemKind.File,"a.txt","folder/a.txt",1,new string('a',64),"text/plain",1);
var initial=TransferManifestPolicy.Create(Guid.Parse("22222222-2222-3333-4444-555555555555"),[item],nowUtc:DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
var cases=new List<object>();
foreach(var name in new[]{"valid","empty-file","wrong-chunks","null-chunks","negative-size","oversize","bad-hash","upper-hash","bad-kind","empty-id","blank-mime","long-mime","display-trailing-slash","duplicate-id","duplicate-path","noncanonical-path","bad-total","offset-time","minor-version","major-version","chunk-min","chunk-low","chunk-high","empty-items"}) {
 var value=initial;
 var changed=name switch {
 "empty-file"=>item with {Size=0,ChunkCount=0}, "wrong-chunks"=>item with {ChunkCount=2}, "null-chunks"=>item with {ChunkCount=null},
 "negative-size"=>item with {Size=-1}, "oversize"=>item with {Size=8L*1024*1024*1024+1,ChunkCount=2049},
 "bad-hash"=>item with {Sha256=new string('g',64)}, "upper-hash"=>item with {Sha256=item.Sha256.ToUpperInvariant()},
 "bad-kind"=>item with {Kind=(TransferItemKind)6}, "empty-id"=>item with {Id=Guid.Empty}, "blank-mime"=>item with {MimeType="\u0085"},
 "long-mime"=>item with {MimeType=new string('a',129)}, "display-trailing-slash"=>item with {DisplayName="a/"},
 "noncanonical-path"=>item with {RelativePath="folder//a.txt/"}, _=>item};
 value=value with {Items=[changed],TotalBytes=changed.Size};
 value=name switch {
 "duplicate-id"=>value with {Items=[item,item with{RelativePath="other.txt"}],TotalBytes=2},
 "duplicate-path"=>value with {Items=[item,item with{Id=Guid.NewGuid(),RelativePath="FOLDER/A.TXT"}],TotalBytes=2},
 "bad-total"=>value with {TotalBytes=2}, "offset-time"=>value with {CreatedAtUtc=value.CreatedAtUtc.ToOffset(TimeSpan.FromHours(8))},
 "minor-version"=>value with {Protocol=new(1,99)}, "major-version"=>value with {Protocol=new(2,0)},
 "chunk-min"=>value with {ChunkBytes=65536}, "chunk-low"=>value with {ChunkBytes=65535}, "chunk-high"=>value with {ChunkBytes=16777217},
 "empty-items"=>value with {Items=[],TotalBytes=0}, _=>value};
 bool valid=true; try{TransferManifestPolicy.Validate(value);}catch{valid=false;}
 cases.Add(new{name,manifest=value,valid});
}
var paths=new List<object>();
foreach(var input in new[]{"a//b/","a\\b","/a","\\\\host\\file","C:relative","a/../b","a/./b","a/\0b","a:colon","a?question","a\u001fb","a/ ","a/",".hidden","CON","é","e\u0301"}) {
 string? result=null;try{result=TransferManifestPolicy.NormalizeRelativePath(input);}catch{}
 paths.Add(new{input,result});
}
var comparisons=new[]{new[]{"a","A"},new[]{"é","É"},new[]{"é","e\u0301"},new[]{"ß","SS"},new[]{"ß","ẞ"},new[]{"i","ı"},new[]{"s","ſ"},new[]{"k","K"},new[]{"σ","ς"},new[]{"𐐀","𐐨"}}.Select(pair=>new{left=pair[0],right=pair[1],equal=StringComparer.OrdinalIgnoreCase.Equals(pair[0],pair[1])});
var wires=new List<object>();
void Wire(string name,JsonNode node) {
 var json=node.ToJsonString(); bool decoded=false,valid=false; string? timestamp=null;
 try { var manifest=JsonSerializer.Deserialize<TransferManifest>(json,options)!; decoded=true; timestamp=manifest.CreatedAtUtc.ToString("O"); TransferManifestPolicy.Validate(manifest); valid=true; } catch {}
 wires.Add(new{name,json,decoded,valid,timestamp});
}
var zero=initial with {Items=[item with {Size=0,ChunkCount=0}],TotalBytes=0};
foreach(var field in new[]{"sessionId","protocol","items","totalBytes","createdAtUtc","chunkBytes"}) {
 var node=JsonSerializer.SerializeToNode(zero,options)!; node.AsObject().Remove(field); Wire("missing-manifest-"+field,node);
 node=JsonSerializer.SerializeToNode(zero,options)!; node[field]=null; Wire("null-manifest-"+field,node);
}
foreach(var field in new[]{"id","kind","displayName","relativePath","size","sha256","mimeType","chunkCount"}) {
 var node=JsonSerializer.SerializeToNode(zero,options)!; node["items"]![0]!.AsObject().Remove(field); Wire("missing-item-"+field,node);
 node=JsonSerializer.SerializeToNode(zero,options)!; node["items"]![0]![field]=null; Wire("null-item-"+field,node);
}
foreach(var field in new[]{"major","minor"}) {
 var node=JsonSerializer.SerializeToNode(zero,options)!; node["protocol"]!.AsObject().Remove(field); Wire("missing-protocol-"+field,node);
 node=JsonSerializer.SerializeToNode(zero,options)!; node["protocol"]![field]=null; Wire("null-protocol-"+field,node);
}
foreach(var date in new[]{"0001-01-01T00:00:00+00:00","9999-12-31T23:59:59.9999999Z","2026-02-29T00:00:00Z","2024-02-29T00:00:00Z","2026-10-20T00:00:60Z","2026-10-20T24:00:00Z","2026-10-20t00:00:00z","2026-10-20T00:00:00+14:01","2026-10-20T00:00:00+14:00","2026-10-20T00:00:00.1234567890123456Z","2026-10-20T00:00:00.12345678901234567Z","2026-10-20T00:00Z","2026-10-20","2026-10-20T00:00:00","2026-10-20T00:00:00Ztrailing","0001-01-01T00:00:00+08:00"}) {
 var node=JsonSerializer.SerializeToNode(zero,options)!; node["createdAtUtc"]=date; Wire("date-"+date,node);
}
File.WriteAllText("results.json",JsonSerializer.Serialize(new{host=System.Runtime.InteropServices.RuntimeInformation.OSDescription,cases,paths,comparisons,wires},options));
''')
env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_GENERATE_ASPNET_CERTIFICATE='false',DOTNET_CLI_HOME=str(root/'macos/build/dotnet-cli'))
subprocess.run([str(pathlib.Path(args.dotnet).resolve()),'run','--project','Fixture.csproj'],cwd=scratch,env=env,check=True)
result=json.loads((scratch/'results.json').read_text());result['baseline']=baseline
result['filenameNote']='Actual fixed C# executed on macOS; Path.GetInvalidFileNameChars results are macOS-specific, not a Windows runtime test.'
destination=root/'macos/Tests/DropSpaceInfrastructureTests/Fixtures/transfer-baseline.json'
destination.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n');print(destination)
