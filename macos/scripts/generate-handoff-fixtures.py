#!/usr/bin/env python3
"""Execute only fixed-baseline handoff policy; no Windows application build.

Pass a .NET 10 SDK executable. Output is deterministic except SDK metadata;
the production Mac app never loads .NET or these generated C# files.
"""
import argparse, json, pathlib, subprocess, os

root = pathlib.Path(__file__).resolve().parents[2]
baseline = '1cd4620dd58a67b0963b73487743137813b8864a'
parser = argparse.ArgumentParser()
parser.add_argument('--dotnet', required=True)
args = parser.parse_args()
scratch = root / 'macos/build/HandoffBaselineFixture'
scratch.mkdir(parents=True, exist_ok=True)
def source(path):
    return subprocess.check_output(['git', 'show', baseline + ':' + path], cwd=root).decode('utf-8-sig')
policy = source('src/DropSpace.Core/Transfer/HandoffMessagePolicy.cs')
models = source('src/DropSpace.Core/Transfer/TransferModels.cs')
(scratch / 'HandoffMessagePolicy.cs').write_text(policy)
(scratch / 'Models.cs').write_text('namespace DropSpace.Core.Transfer;\n' + models[models.index('public enum HandoffMessageKind'):models.index('public sealed record ClipboardSyncPreference')])
(scratch / 'HandoffFixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
inputs = [
    (1, 'line 1\r\nline 2\rlast'), (1, '\u200b'), (1, '\u0085'), (1, 'a\0b'),
    (2, 'https://example.test/path#fragment'), (2, 'HTTPS://EXAMPLE.TEST:443'),
    (2, 'http://EXAMPLE.test:80/a/../b/./c?x=%7E#key'),
    (2, 'https://example.test/%7e/%41/%2f/%2e%2e/b'),
    (2, 'https://例子.测试/路径?q=你好#密钥'), (2, 'https://example.test/a\\b'),
    (2, 'http://127.1'), (2, 'http://[::1]:80/a'), (2, 'http://user:p%40ss@EXAMPLE.test'),
    (2, 'https://example.test/a%zz'), (2, 'https://example.test/a b'), (2, 'mailto:a@example.test'),
    (2, 'https://example.test/%F0%9F%98%80#%7E'), (2, 'https://example.test/é/e\u0301'),
    (2, 'https://example.test/a?x=1&x=2#k+/='), (2, 'https://example.test/a\0b'),
]
(scratch / 'inputs.json').write_text(json.dumps([{'kind': k, 'payload': p} for k,p in inputs], ensure_ascii=False))
(scratch / 'Program.cs').write_text('''using System.Text.Json;
using DropSpace.Core.Transfer;
var inputs = JsonDocument.Parse(File.ReadAllText("inputs.json")).RootElement;
var rows = new List<object>();
foreach (var input in inputs.EnumerateArray()) {
    var kind = input.GetProperty("kind").GetInt32(); var payload = input.GetProperty("payload").GetString()!;
    try {
        var message = HandoffMessagePolicy.Create(Guid.Parse("11111111-2222-3333-4444-555555555555"), " Fixture ", (HandoffMessageKind)kind, payload, " label ", DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
        rows.Add(new { kind, payload, accepted=true, normalized=message.Utf8Payload, bytes=message.ByteLength, hash=message.Sha256 });
    } catch (Exception error) { rows.Add(new { kind, payload, accepted=false, error=error.GetType().Name }); }
}
File.WriteAllText("results.json", JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented=true }));
''')
environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false')
subprocess.run([str(pathlib.Path(args.dotnet).resolve()), 'run', '--project', 'HandoffFixture.csproj'], cwd=scratch, env=environment, check=True)
output = {'baseline': baseline, 'sdk': subprocess.check_output([args.dotnet, '--version'], cwd=root).decode().strip(), 'cases': json.loads((scratch / 'results.json').read_text())}
destination = root / 'macos/Tests/DropSpaceInfrastructureTests/Fixtures/handoff-baseline.json'
destination.write_text(json.dumps(output, ensure_ascii=False, indent=2) + '\n')
print(destination)
