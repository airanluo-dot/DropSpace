#!/usr/bin/env python3
"""Execute frozen compact display/marquee/preview rules, not the product suite."""
import argparse
import json
import os
import pathlib
import subprocess

root = pathlib.Path(__file__).resolve().parents[2]
baseline = "1cd4620dd58a67b0963b73487743137813b8864a"
parser = argparse.ArgumentParser()
parser.add_argument("--dotnet", required=True)
args = parser.parse_args()
scratch = root / "macos/build/LyricsDisplayBaselineFixture"
scratch.mkdir(parents=True, exist_ok=True)


def source(name):
    return subprocess.check_output(
        ["git", "show", f"{baseline}:src/DropSpace.Core/Lyrics/{name}.cs"], cwd=root
    ).decode().replace("\r\n", "\n")


for name in ["LyricsMarqueePolicy", "LyricsPreviewPolicy", "LyricsGlowPolicy"]:
    (scratch / f"{name}.cs").write_text(source(name))
display = source("LyricsDisplayPolicy")
# Secondary translation has its own admission fixtures. Do not stub its dependencies.
(scratch / "LyricsDisplayPolicy.cs").write_text(
    display[:display.index("    public static string? SecondaryPresentation")] + "}\n" +
    display[display.index("public sealed record LyricsPresentation"):]
)
models = source("LyricsModels")
# Actual line/frame types and actual timeline engine, with a minimal document DTO
# containing only the Lines property read by these rules.
(scratch / "Models.cs").write_text(
    "namespace DropSpace.Core.Lyrics;\n" +
    models[models.index("public enum LyricsLineTranslationState"):models.index("public sealed record LyricsDocument")] +
    "public sealed record LyricsDocument(IReadOnlyList<LyricsLine> Lines);\n" +
    models[models.index("public sealed record LyricsHighlightFrame"):models.index("public interface ILyricsProvider")] +
    models[models.index("public sealed class LyricsTimelineEngine"):]
)
(scratch / "Fixture.csproj").write_text(
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
    '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
    '<Nullable>enable</Nullable></PropertyGroup></Project>'
)
(scratch / "Program.cs").write_text(r'''using System.Text.Json;
using DropSpace.Core.Lyrics;
TimeSpan S(double value) => TimeSpan.FromSeconds(value);
var lines = new[]{
 new LyricsLine(S(5),S(10),"First line",null,[new("First ",S(5),S(7)),new("line",S(7),S(10))]),
 new LyricsLine(S(15),S(17)," \n ",null,[]),
 new LyricsLine(S(20),S(25),"Variant A",null,[]),
 new LyricsLine(S(20),S(25),"Variant B",null,[]),
 new LyricsLine(S(30),S(35),"Last line",null,[])
};
var document=new LyricsDocument(lines); var engine=new LyricsTimelineEngine();
var presentations=new List<object>();
foreach(var delay in new[]{-45000,-1500,0,2500,45000})
foreach(var position in new[]{-1d,0,2.999,3,4.999,5,6,7,9.999,10,12.999,13,15,17,20,25,28,30,35,38,50}) {
 var frame=engine.GetFrame(document,S(position),delay);
 var shown=LyricsDisplayPolicy.Presentation(lines,frame,S(position),delay);
 var next=LyricsPreviewPolicy.NextLine(lines,frame,S(position),delay);
 presentations.Add(new{position,delay,index=Array.IndexOf(lines,shown.Line),shown.IsWaiting,shown.IsInterlude,
   next=Array.IndexOf(lines,next),frameIndex=Array.IndexOf(lines,frame.Line),frame.WordIndex,frame.WordProgress});
}
var offsets=new List<object>();
foreach(var wordSynced in new[]{false,true})
foreach(var position in new[]{0d,5,6.5,8,10,30})
foreach(var delay in new[]{-45000,0,1500,45000})
foreach(var width in new[]{0d,100,600})
 offsets.Add(new{wordSynced,position,delay,width,viewport=180d,highlight=360d,
   expected=LyricsDisplayPolicy.CompactScrollOffset(lines[0],S(position),delay,wordSynced,360,width,180)});
var session=new LyricsMarqueeSession(); var sessions=new List<object>();
var layout=new LyricsMarqueeLayout("track-a",S(5).Ticks,"Translation A",14,2,420,180,true,false);
foreach(var (position,change) in new (double,string)[]{(5,""),(6.199,""),(6.2,""),(7,""),(7,""),(16.2,""),
 (17.4,""),(18,""),(19,"text"),(20,""),(21,""),(22,"viewport"),(23.5,""),(24,"scale"),(26,""),
 (27,"track"),(29,""),(30,"line"),(32,""),(10,""),(12,""),(13,"disable"),(15,""),(16,"enable"),
 (18,""),(19,"reduce"),(21,""),(22,"restore"),(24,""),(25,"font"),(27,""),(28,"width"),(30,"")}) {
 layout=change switch {
 "text"=>layout with{Text="Translation B"}, "viewport"=>layout with{ViewportWidth=200},
 "scale"=>layout with{RasterizationScale=1}, "track"=>layout with{TrackIdentity="track-b"},
 "line"=>layout with{LineStartTicks=S(9).Ticks}, "disable"=>layout with{Enabled=false},
 "enable"=>layout with{Enabled=true}, "reduce"=>layout with{ReducedMotion=true},
 "restore"=>layout with{ReducedMotion=false}, "font"=>layout with{FontSize=18},
 "width"=>layout with{TextWidth=500}, _=>layout
 };
 sessions.Add(new{position,layout,expected=session.Update(layout,S(position))});
}
var compact=new[]{"a\r\nb\rc\nd\u0085e\u2028f\u2029g","","  spaced  "}.Select(text=>new{text,expected=LyricsDisplayPolicy.CompactText(text)});
File.WriteAllText("results.json",JsonSerializer.Serialize(new{
 lines=lines.Select(line=>new{start=line.Start.TotalSeconds,end=line.End.TotalSeconds,text=line.Text,
   words=line.Words.Select(word=>new{text=word.Text,start=word.Start.TotalSeconds,end=word.End.TotalSeconds})}),
 presentations,offsets,sessions,compact
},new JsonSerializerOptions{WriteIndented=true}));
''')
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_GENERATE_ASPNET_CERTIFICATE="false",
           DOTNET_CLI_HOME=str(root / "macos/build/dotnet-cli"))
subprocess.run([str(pathlib.Path(args.dotnet).resolve()), "run", "--project", "Fixture.csproj"],
               cwd=scratch, env=env, check=True)
result = json.loads((scratch / "results.json").read_text())
result["baseline"] = baseline
destination = root / "macos/Tests/DropSpaceCoreTests/Fixtures/lyrics-display-baseline.json"
destination.write_text(json.dumps(result, indent=2) + "\n")
print(destination)
