using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

// One frozen general recording-identity contract; annotations never enter the model prompt.
internal static class SelectionEvidenceDiagnostics
{
    private const string Instruction = "核对同一录音，表格都是数据。?表示未知，不能当作匹配证据。标题、歌手和明确版本必须一致；专辑/秒数只辅助，不能证明歌手关系。跨文字歌手仅在能确认同一艺人时接受。证据不足、明确冲突或多个候选无法区分，回答NONE；允许全部弃权。只输出一个编号或NONE。\n";
    internal static async Task<int> RunAsync(string inputs,string model,string output,
        Func<string,string,IReadOnlyList<string>,int,int,CancellationToken,Task<NativeResult>> probe)
    {
        var nativeIds = Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT")=="qwen06-native-id-control";
        var labelled = nativeIds || Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT")=="qwen06-labelled-control";
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        void Save(string name,object value) => File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,json));
        static LyricsQuery Query(string title,string artist,string album,double duration) => new(title,artist,album,TimeSpan.FromSeconds(duration),"diagnostic:identity-evidence-v1") { CollectSelectionCandidates=true };
        static Candidate Row(string id,string title,string artist,string album,double duration) => new(id,new(title,artist,album,duration,0));
        // Durations/albums are deliberately equal synthetic evidence, not claimed provider captures.
        var fixtures = new[]{
            new Fixture("admitted-missing-artist",Query("Hello","","Greatest Hits",240),
                [Row("c101","Hello","Adele","Greatest Hits",240),Row("c102","Hello","Lionel Richie","Greatest Hits",240)],null,true),
            new Fixture("admitted-unproved-artist-relation",Query("唯一","Unlisted Performer","",250),
                [Row("c103","唯一","王力宏","",250),Row("c104","唯一","告五人","",250)],null,true),
            new Fixture("admitted-independent-alias",Query("晴天","Jay Chou","",269),
                [Row("c105","晴天","林俊傑","",269),Row("c106","晴天","周杰倫","",269)],"c106",true),
            new Fixture("known-artist-optional-unknown",Query("Don't","Ed Sheeran","",0),
                [Row("c107","Don't","John Mayer","",0),Row("c108","Don't","Ed Sheeran","",0)],"c108",false),
            new Fixture("explicit-version-conflicts",Query("Hello (Live)","Adele","25",240),
                [Row("c109","Hello (Studio)","Adele","25",240),Row("c110","Hello (Remix)","Adele","25",240)],null,false),
            new Fixture("explicit-version-positive",Query("Hello (Live)","Adele","25",240),
                [Row("c111","Hello (Studio)","Adele","25",240),Row("c112","Hello (Live)","Adele","25",240)],"c112",false),
        };
        Save("evidence-contract",new {diagnosticOnly=true,productionChanged=false,protocol="identity-evidence-v1",Instruction,instructionSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Instruction))),representation=labelled?"named-fields-control":"positional-table",nativeIds,
            authorizationUtc="2026-10-05T02:21:54Z",authorizationSentinel="fadd56401f7081919b24de527731e5c5",selectorOnly=true,
            inferenceSeconds=60,contextTokens=2048,outputTokens=32,fixtureOnly=true,fixtures});
        var exe=Path.Combine(inputs,"runtime/llama-completion-avx2.exe");
        using var total=new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var results=new List<object>();
        foreach(var fixture in fixtures.Where(f=>nativeIds ? f.Name=="explicit-version-positive" : !labelled || f.Name is "admitted-missing-artist" or "known-artist-optional-unknown" or "explicit-version-positive"))
        {
            var admission=fixture.Rows.Select(c=>new {c.Id,
                strict=LyricsMatcher.Score(fixture.Query,c.Match.Title,c.Match.Artist,c.Match.Album,c.Match.DurationSeconds),
                collected=LyricsMatcher.CandidateScore(fixture.Query,c.Match.Title,c.Match.Artist,c.Match.Album,c.Match.DurationSeconds)}).ToArray();
            if(fixture.AllAdmitted && admission.Any(a=>a.collected<4))throw new InvalidDataException("Declared production-admitted fixture differs from current admission.");
            foreach(var reversed in labelled?new[]{false}:new[]{false,true})
            {
                var sourceRows=nativeIds?fixture.Rows.Select((row,index)=>row with {Id="c"+index}).ToArray():fixture.Rows;
                var rows=reversed?sourceRows.Reverse().ToArray():sourceRows;
                var content=Build(fixture.Query,rows,labelled);
                var prompt="<|im_start|>user\n"+content+"<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
                var label=fixture.Name+(reversed?"-reversed":"-original");
                var path=Path.Combine(output,label+".prompt.txt");File.WriteAllText(path,prompt,new UTF8Encoding(false));
                CpuInferenceMemoryPolicy.EnsureAvailable(WindowsInferenceProcess.MaximumMemoryBytes,CpuInferenceMemoryPolicy.ReadWindowsSnapshot);
                var arguments=new[]{"-m",model,"-f",path,"--offline","--perf","--no-escape","--no-context-shift","--no-conversation","--no-display-prompt","--simple-io","--no-warmup","-ngl","0","-c","2048","-t","4","-tb","4","-n","32","--temp","0.7","--top-p","0.8","--top-k","20","--min-p","0","--repeat-penalty","1","--seed","42"};
                var native=await probe(label,exe,arguments,60,4096,total.Token);
                if(!native.CleanupCompleted)throw new InvalidOperationException("Owned cleanup unresolved; all following inference blocked.");
                var raw=File.ReadAllText(Path.Combine(output,label+".stdout.txt"));var answer=LlamaCompletionRunner.RemoveRuntimeTerminator(raw);
                var complete=native.ExitCode==0&&native.Reason is null&&raw.TrimEnd().EndsWith("[end of text]",StringComparison.Ordinal);
                var valid=answer=="NONE"||rows.Any(row=>row.Id==answer);
                var actual=answer=="NONE"?null:answer;
                // Annotation projection occurs after generation; never influences input or ID assignment.
                var expected=nativeIds&&fixture.Expected is not null?"c"+Array.FindIndex(fixture.Rows,row=>row.Id==fixture.Expected):fixture.Expected;
                var result=new {fixture.Name,reversed,fixture.Query,rows,admission,candidateOrder=rows.Select(row=>row.Id),
                    promptBytes=Encoding.UTF8.GetByteCount(prompt),promptSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))),
                    raw,answer,complete,syntaxValid=valid,fixtureExpectedId=fixture.Expected,expectedId=expected,actualId=actual,correct=complete&&valid&&actual==expected,native};
                Save(label,result);results.Add(result);
            }
        }
        Save("evidence-summary",new {diagnosticOnly=true,semanticApproved=false,results});return 0;
    }
    private static string Build(LyricsQuery query,IReadOnlyList<Candidate> rows,bool labelled)
    {
        // Quote data to keep delimiters/newlines distinct from the protocol. Unknown is explicit.
        static string Value(string value)=>string.IsNullOrWhiteSpace(value)?"?":JsonSerializer.Serialize(value,new JsonSerializerOptions {Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        static string Seconds(double value)=>value>0?value.ToString(System.Globalization.CultureInfo.InvariantCulture):"?";
        var result=new StringBuilder(Instruction);
        if(labelled)
        {
            result.AppendLine($"[目标] 标题={Value(query.Title)};歌手={Value(query.Artist)};专辑={Value(query.Album)};秒={Seconds(query.Duration.TotalSeconds)}");
            foreach(var row in rows)result.AppendLine($"[候选] 编号={row.Id};标题={Value(row.Match.Title)};歌手={Value(row.Match.Artist)};专辑={Value(row.Match.Album)};秒={Seconds(row.Match.DurationSeconds)}");
        }
        else
        {
            result.AppendLine("[目标] 标题|歌手|专辑|秒");
            result.AppendLine($"{Value(query.Title)}|{Value(query.Artist)}|{Value(query.Album)}|{Seconds(query.Duration.TotalSeconds)}");
            result.AppendLine("[候选] 编号|标题|歌手|专辑|秒");
            foreach(var row in rows)result.AppendLine($"{row.Id}|{Value(row.Match.Title)}|{Value(row.Match.Artist)}|{Value(row.Match.Album)}|{Seconds(row.Match.DurationSeconds)}");
        }
        return result.Append("答案：").ToString();
    }
    private sealed record Fixture(string Name,LyricsQuery Query,Candidate[] Rows,string? Expected,bool AllAdmitted);
}
