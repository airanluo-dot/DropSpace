#!/usr/bin/env python3
"""Run only the immutable Windows clipboard policy to produce comparison vectors."""
import argparse, json, os, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
baseline = '1cd4620dd58a67b0963b73487743137813b8864a'
parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', required=True); args = parser.parse_args()
scratch = root / 'macos/build/ClipboardBaselineFixture'; scratch.mkdir(parents=True, exist_ok=True)
def source(path): return subprocess.check_output(['git', 'show', baseline + ':' + path], cwd=root).decode('utf-8-sig')
policy = source('src/DropSpace.Core/Transfer/TransferPolicies.cs')
models = source('src/DropSpace.Core/Transfer/TransferModels.cs')
(scratch / 'Policy.cs').write_text('using System.Security.Cryptography;\nusing System.Text;\nnamespace DropSpace.Core.Transfer;\n' + policy[policy.index('public static class ClipboardEnvelopePolicy'):])
(scratch / 'Models.cs').write_text('using System.Text.Json.Serialization;\nnamespace DropSpace.Core.Transfer;\n' + models[models.index('public enum ClipboardPayloadKind'):models.index('public enum TransferItemKind')] + models[models.index('public sealed record ClipboardEnvelope'):models.index('public enum HandoffMessageKind')] + models[models.index('public enum ClipboardSyncMode'):models.index('public sealed record ShareLimits')])
(scratch / 'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
cases = [dict(name='line-endings', text='a\r\nb\rc'), dict(name='url-raw', text='HTTPS://EXAMPLE.test:443/a#keep', kind=2), dict(name='not-url',text='hello',kind=2), dict(name='blank',text='\u0085'), dict(name='zero-width',text='\u200b'), dict(name='emoji',text='é e\u0301😀'), dict(name='automatic-exact',text='a',repeat=262144), dict(name='automatic-over',text='a',repeat=262145), dict(name='hard-exact',text='a',repeat=2097152), dict(name='hard-over',text='a',repeat=2097153), dict(name='wrong-kind',text='hello',kind=3), dict(name='image',image=True), dict(name='image-empty',image=True,repeat=0)]
for mutation in ['empty-event','empty-origin','negative-sequence','zero-bytes','bad-hash','upper-hash','blank-mime','long-mime','offset','ancient','opposite-payload']:
    cases.append(dict(name=mutation, text='valid', mutation=mutation))
(scratch / 'inputs.json').write_text(json.dumps(cases, ensure_ascii=False))
(scratch / 'Program.cs').write_text('''using System.Text.Json;
using DropSpace.Core.Transfer;
var inputs = JsonDocument.Parse(File.ReadAllText("inputs.json")).RootElement;
var output = new List<object>(); var now = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
var origin = Guid.Parse("11111111-2222-3333-4444-555555555555");
foreach (var input in inputs.EnumerateArray()) {
    bool created=false, valid=false; ClipboardEnvelope? envelope=null;
    try {
        int repeat=input.TryGetProperty("repeat",out var r)?r.GetInt32():1;
        bool image=input.TryGetProperty("image",out var i)&&i.GetBoolean();
        envelope=image ? ClipboardEnvelopePolicy.CreateImage(origin,1,Enumerable.Repeat((byte)65,repeat).ToArray(),"image/png",now) : ClipboardEnvelopePolicy.CreateText(origin,1,string.Concat(Enumerable.Repeat(input.GetProperty("text").GetString(),repeat)),(ClipboardPayloadKind)(input.TryGetProperty("kind",out var k)?k.GetInt32():1),now);
        created=true;
        if(input.TryGetProperty("mutation",out var m)) envelope=m.GetString() switch {
            "empty-event"=>envelope with {EventId=Guid.Empty}, "empty-origin"=>envelope with {OriginDeviceId=Guid.Empty},
            "negative-sequence"=>envelope with {OriginSequence=-1}, "zero-bytes"=>envelope with {ByteLength=0},
            "bad-hash"=>envelope with {Sha256=new string('0',64)}, "upper-hash"=>envelope with {Sha256=envelope.Sha256.ToUpperInvariant()},
            "blank-mime"=>envelope with {Mime=" "}, "long-mime"=>envelope with {Mime=new string('a',129)},
            "offset"=>envelope with {CreatedAtUtc=now.ToOffset(TimeSpan.FromHours(8))}, "ancient"=>envelope with {CreatedAtUtc=DateTimeOffset.MinValue},
            "opposite-payload"=>envelope with {ImageBytes=new byte[]{1,2}}, _=>envelope};
        ClipboardEnvelopePolicy.Validate(envelope); valid=true;
    } catch { }
    output.Add(new {input,created,valid,hash=envelope?.Sha256,bytes=envelope?.ByteLength,mime=envelope?.Mime,automatic=envelope is null?null:Enumerable.Range(0,4).Select(mode=>ClipboardEnvelopePolicy.IsAllowedAutomatically(envelope,(ClipboardSyncMode)mode)).ToArray()});
}
File.WriteAllText("results.json",JsonSerializer.Serialize(output,new JsonSerializerOptions {WriteIndented=true}));
''')
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false', DOTNET_CLI_HOME=str(root / 'macos/build/dotnet-cli'))
subprocess.run([str(pathlib.Path(args.dotnet).resolve()), 'run', '--project', 'Fixture.csproj'], cwd=scratch, env=env, check=True)
output = dict(baseline=baseline, sdk=subprocess.check_output([args.dotnet,'--version'],env=env).decode().strip(), cases=json.loads((scratch/'results.json').read_text()))
destination=root/'macos/Tests/DropSpaceInfrastructureTests/Fixtures/clipboard-baseline.json'
destination.write_text(json.dumps(output,ensure_ascii=False,indent=2)+'\n'); print(destination)
