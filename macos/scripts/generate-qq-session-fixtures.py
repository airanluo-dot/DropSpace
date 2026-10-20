#!/usr/bin/env python3
"""Run only the frozen QQ cookie/identity/header rules with synthetic credentials."""
import argparse
import json
import os
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
baseline = '1cd4620dd58a67b0963b73487743137813b8864a'
parser = argparse.ArgumentParser()
parser.add_argument('--dotnet', required=True)
args = parser.parse_args()
scratch = root / 'macos/build/QQSessionBaselineFixture'
scratch.mkdir(parents=True, exist_ok=True)
(scratch / 'QqMusicSession.cs').write_bytes(subprocess.check_output(
    ['git', 'show', baseline + ':src/DropSpace.Infrastructure/Lyrics/QqMusicSession.cs'], cwd=root))
(scratch / 'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
# Storage is not exercised; the Windows-only DPAPI dependency must never be
# mistaken for a native storage test or be executed by this fixture runner.
(scratch / 'Stubs.cs').write_text('''
namespace System.Security.Cryptography {
 public enum DataProtectionScope { CurrentUser }
 public static class ProtectedData {
  public static byte[] Protect(byte[] data,byte[]? entropy,DataProtectionScope scope)=>throw new PlatformNotSupportedException();
  public static byte[] Unprotect(byte[] data,byte[]? entropy,DataProtectionScope scope)=>throw new PlatformNotSupportedException();
 }
}
namespace DropSpace.Infrastructure.Lyrics {
 public sealed class LyricsProviderRejectedException(string message,int code):Exception(message) { public int Code=>code; }
}
''')
(scratch / 'Program.cs').write_text('''
using System.Reflection;
using System.Text.Json;
using DropSpace.Infrastructure.Lyrics;
static QqMusicCookie C(string value="synthetic-key",string domain=".qq.com",string name="qqmusic_key",string path="/",double? expires=null)
 =>new(){Name=name,Value=value,Domain=domain,Path=path,Expires=expires,HttpOnly=true};
QqMusicCookie[][] inputs=[
 [], [C(),C("oo123456",name:"uin")], [C("old"),C("new"),C("007",name:"qqmusic_uin")],
 [C(name:"qm_keyst"),C("o42",name:"wxuin")], [C(),C("invalid",name:"uin")],
 [C(domain:"y.qq.com")], [C(domain:".y.qq.com")], [C(domain:"u.y.qq.com")],
 [C(domain:".qq.com.attacker.test")], [C(domain:".QQ.COM")], [C(path:"/other")],
 [C(name:"skey")], [C("bad;value")], [C("bad\\\\value")], [C("bad,value")],
 [C(new string('a',4096))], [C(new string('a',4097))], [C(expires:0)],
 Enumerable.Repeat(C(name:"skey"),64).Append(C()).ToArray(),
 [C("one",domain:".qq.com"),C("two",domain:".y.qq.com"),C("o1",name:"uin"),C("o2",name:"qqmusic_uin")],
 [C("two",domain:".y.qq.com"),C("one",domain:".qq.com"),C("three",domain:"u.y.qq.com"),C("o1",name:"uin"),C("o2",name:"qqmusic_uin",domain:".y.qq.com")],
 [C("three",domain:"u.y.qq.com"),C("one",domain:".qq.com"),C("two",domain:".y.qq.com"),C("o1",name:"uin"),C("o2",name:"qqmusic_uin",domain:".y.qq.com")],
 [C("two",domain:".y.qq.com"),C("three",domain:"u.y.qq.com"),C("one",domain:".qq.com"),C("o2",name:"qqmusic_uin",domain:".y.qq.com"),C("o1",name:"uin")]
];
var urls=new[]{"https://u.y.qq.com/cgi-bin/musicu.fcg","https://c.y.qq.com/","https://y.qq.com/","http://u.y.qq.com/","https://u.y.qq.com:444/","https://user@u.y.qq.com/","https://u.y.qq.com.attacker.test/"};
object Export(QqMusicCookie c)=>new{name=c.Name,value=c.Value,domain=c.Domain,path=c.Path,expires=c.Expires,httpOnly=c.HttpOnly};
var cases=new List<object>();
foreach(var input in inputs) {
 var accepted=QqMusicSession.Validate(input);var session=new QqMusicSession("unused");
 typeof(QqMusicSession).GetField("_cookies",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(session,accepted);
 var identity=session.Identity();
 cases.Add(new{input=input.Select(Export),accepted=accepted.Select(Export),usable=QqMusicSession.HasUsableSession(input),
  uin=identity.Uin,gtk=identity.Gtk,headers=urls.Select(url=>new{url,header=session.CookieHeader(new Uri(url))})});
}
File.WriteAllText("results.json",JsonSerializer.Serialize(cases,new JsonSerializerOptions{WriteIndented=true}));
''')
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false',
           DOTNET_CLI_HOME=str(root / 'macos/build/dotnet-cli'))
subprocess.run([str(Path(args.dotnet).resolve()), 'run', '--project', 'Fixture.csproj'],
               cwd=scratch, env=env, check=True)
result = {'baseline': baseline, 'cases': json.loads((scratch / 'results.json').read_text())}
destination = root / 'macos/Tests/DropSpaceInfrastructureTests/Fixtures/qq-session-baseline.json'
destination.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
print('Frozen cookie/identity/header cases:', len(result['cases']))
