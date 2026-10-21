#!/usr/bin/env python3
"""Run the frozen C# Nearby range/header rules, without starting its web host."""
import argparse, json, os, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
baseline = '1cd4620dd58a67b0963b73487743137813b8864a'
parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', required=True); args = parser.parse_args()
scratch = root/'macos/build/NearbyBaselineFixture'; scratch.mkdir(parents=True, exist_ok=True)
source = subprocess.check_output(['git','show',baseline+':src/DropSpace.Infrastructure/Sharing/NearbyShareServer.cs'],cwd=root).decode('utf-8-sig')
ranges = source[source.index('    internal static bool ParseRange'):source.index('    private string GetPrivateAddress')]
headers = source[source.index('    private static string EscapeHeader'):source.index('    public ValueTask DisposeAsync')]
(scratch/'Rules.cs').write_text('using System.Globalization;\nstatic class Rules {\n'+ranges+headers+'\n}')
(scratch/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>')
(scratch/'Program.cs').write_text(r'''using System.Text.Json;
var ranges=new List<object>();
foreach(var total in new long[]{0,1,10,long.MaxValue}) foreach(var value in new[]{"0-","0-0","1-9","7-100","-1","-100","-0","-","0-1,3-4","+1-2"," 1-2","1-2 ","01-02","0--1","9223372036854775808-","0-9223372036854775807","0-9223372036854775808","9-8","10-10"}) {
 var valid=Rules.ParseRange(value,total,out var start,out var end); ranges.Add(new{value,total,valid,start,end});
}
var headers=new[]{"simple.txt","中文 & <file>.txt","quote\" and slash\\","a\r\nb\u0001","é😀.txt","a'b ();%?#.txt"}.Select(name=>new{name,value=Rules.CreateDownloadDisposition(name)});
File.WriteAllText("results.json",JsonSerializer.Serialize(new{ranges,headers}));
''')
env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_GENERATE_ASPNET_CERTIFICATE='false',DOTNET_CLI_HOME=str(root/'macos/build/dotnet-cli'))
subprocess.run([str(pathlib.Path(args.dotnet).resolve()),'run','--project','Fixture.csproj'],cwd=scratch,env=env,check=True)
result=json.loads((scratch/'results.json').read_text()); result['baseline']=baseline
target=root/'macos/Tests/DropSpaceInfrastructureTests/Fixtures/nearby-baseline.json'
target.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n'); print(target)
