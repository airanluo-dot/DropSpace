#!/usr/bin/env python3
"""Execute only the frozen spectrum/freshness/recovery rules, not the app suite."""
import argparse, json, os, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
baseline = '1cd4620dd58a67b0963b73487743137813b8864a'
parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', required=True); args = parser.parse_args()
scratch = root / 'macos/build/SpectrumBaselineFixture'; scratch.mkdir(parents=True, exist_ok=True)
for name in ['SpectrumAnalyzer', 'SpectrumFreshnessPolicy', 'AudioCaptureRecoveryPolicy']:
    (scratch / (name + '.cs')).write_bytes(subprocess.check_output(['git', 'show', f'{baseline}:src/DropSpace.Core/Media/{name}.cs'], cwd=root))
(scratch / 'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
(scratch / 'Program.cs').write_text('''using System.Text.Json;
using DropSpace.Core.Media;
var output = new List<object>();
foreach(var rate in new[]{44100,48000,96000}) foreach(var kind in new[]{"tone","mixed","silence","invalid"}) {
    var analyzer=new SpectrumAnalyzer(rate); var frames=new List<object>();
    for(var i=0;i<8192;i++) {
        double sample=kind switch {
            "tone"=>.2*Math.Sin(2*Math.PI*1000*i/rate),
            "mixed"=>i<4096 ? .3*Math.Sin(2*Math.PI*80*i/rate)+.1*Math.Cos(2*Math.PI*4000*i/rate) : 0,
            "invalid"=>(i%4) switch {0=>double.NaN,1=>double.PositiveInfinity,2=>2.0,_=>-2.0},
            _=>0
        };
        if(analyzer.AddSample(sample) is {} frame) frames.Add(new{bands=frame.Bands,revision=frame.Revision});
    }
    output.Add(new{rate,kind,frames});
}
var baseFrame=new SpectrumFrame(AudioCaptureMode.ProcessLoopback,new double[]{0,.2,.4,.6,.8,1},42);
var freshness=new[]{-1,0,.15,.3,.599,.6,1}.Select(age=>new{age,bands=SpectrumFreshnessPolicy.Apply(baseFrame,TimeSpan.FromSeconds(age)).Bands}).ToArray();
var recovery=new List<object>();
foreach(var wanted in new[]{false,true}) foreach(var mode in new[]{0,1,3}) foreach(var age in new[]{0.0,5.0}) foreach(var attempt in new[]{4.9,5.0})
    recovery.Add(new{wanted,mode,age,attempt,recover=AudioCaptureRecoveryPolicy.ShouldRecover(wanted,(AudioCaptureMode)mode,TimeSpan.FromSeconds(age),TimeSpan.FromSeconds(attempt))});
File.WriteAllText("results.json",JsonSerializer.Serialize(new{cases=output,freshness,recovery},new JsonSerializerOptions{WriteIndented=true}));
''')
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false', DOTNET_CLI_HOME=str(root/'macos/build/dotnet-cli'))
subprocess.run([str(pathlib.Path(args.dotnet).resolve()), 'run', '--project', 'Fixture.csproj'], cwd=scratch, env=env, check=True)
result = json.loads((scratch/'results.json').read_text()); result['baseline'] = baseline
destination = root/'macos/Tests/DropSpaceCoreTests/Fixtures/spectrum-baseline.json'
destination.write_text(json.dumps(result, indent=2)+'\n')
print(destination)
