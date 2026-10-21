#!/usr/bin/env python3
"""Execute only the three frozen glow rules; not the product test suite."""
import argparse, json, os, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
baseline = '1cd4620dd58a67b0963b73487743137813b8864a'
parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', required=True); args = parser.parse_args()
scratch = root / 'macos/build/GlowBaselineFixture'; scratch.mkdir(parents=True, exist_ok=True)
for name in ['LyricsGlowPolicy', 'LyricsGlowEnvelope', 'LyricsGlowAudioResponse']:
    (scratch / (name + '.cs')).write_bytes(subprocess.check_output(['git', 'show', f'{baseline}:src/DropSpace.Core/Lyrics/{name}.cs'], cwd=root))
(scratch / 'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
(scratch / 'Program.cs').write_text('''using System.Text.Json;
using DropSpace.Core.Lyrics;
var eligibility = new List<object>();
foreach(var mode in new[]{0,1,2}) foreach(var playing in new[]{false,true}) foreach(var visible in new[]{false,true})
foreach(var shown in new[]{false,true}) foreach(var origin in new[]{0,1,2}) foreach(var text in new[]{"","  ","translated"})
 eligibility.Add(new{mode,playing,visible,shown,origin,text,expected=LyricsGlowPolicy.IsEligible((LyricsGlowMode)mode,playing,visible,shown,(LyricsTranslationOrigin)origin,text)});
var steps = new List<object>(); var envelope = new LyricsGlowEnvelope(); var audio = new LyricsGlowAudioResponse();
for(var i=0;i<240;i++) {
 var eligible=i<180; var reduced=i>=80&&i<120; var simplified=i>=50&&i<155;
 var energy=i<20?0:i<50?.3:i<80?.95:.1; var bands=new[]{energy,.1,0,.4,.8,0};
 var seconds=i==140?2:0.033;
 if(eligible&&!reduced) audio.Advance(energy,bands,TimeSpan.FromSeconds(seconds)); else audio.Reset();
 envelope.Advance(eligible,audio.Energy,TimeSpan.FromSeconds(seconds),reduced,audio.Bands,simplified);
 steps.Add(new{eligible,reduced,simplified,energy,bands,seconds,audioEnergy=audio.Energy,audioBands=audio.Bands.ToArray(),state=envelope.Capture()});
}
File.WriteAllText("results.json",JsonSerializer.Serialize(new{eligibility,steps},new JsonSerializerOptions{WriteIndented=true}));
''')
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false', DOTNET_CLI_HOME=str(root/'macos/build/dotnet-cli'))
subprocess.run([str(pathlib.Path(args.dotnet).resolve()), 'run', '--project', 'Fixture.csproj'], cwd=scratch, env=env, check=True)
result = json.loads((scratch/'results.json').read_text()); result['baseline'] = baseline
destination = root/'macos/Tests/DropSpaceCoreTests/Fixtures/glow-baseline.json'
destination.write_text(json.dumps(result, indent=2)+'\n'); print(destination)
