using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

if (!OperatingSystem.IsWindows() || args.Length != 3) throw new InvalidOperationException("Explicit cloud Windows inputs/model/output required.");
var thinking=Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT")=="qwen06-thinking";
var inputs=Path.GetFullPath(args[0]); var model=Path.GetFullPath(args[1]); var output=Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
var json=new JsonSerializerOptions {WriteIndented=true};
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,json));
var exe=Path.Combine(inputs,"runtime/llama-completion-avx2.exe");
await CheckHash(exe,"3fc3bd789f4d5a48eea3674182bbb9908eb7f44ca264f8bbf11c4bab526b9783");
await CheckHash(model,"9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031");
if(new FileInfo(model).Length!=639446688) throw new InvalidDataException("Model byte count mismatch.");
using var modelLease=new FileStream(model,FileMode.Open,FileAccess.Read,FileShare.Read);
using var exeLease=new FileStream(exe,FileMode.Open,FileAccess.Read,FileShare.Read);
Save("identity",new {diagnosticOnly=true,productionChanged=false,authorizationUtc="2026-10-05T01:11:06Z",authorizationSentinel="208dd5c270f48191a5f2ac46576d7c28", modelRevision="23749fefcc72300e3a2ad315e1317431b06b590a",modelSha256="9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031",modelBytes=639446688, exeSha256="3fc3bd789f4d5a48eea3674182bbb9908eb7f44ca264f8bbf11c4bab526b9783", originalRuntimeArtifact=11316610580L, chatTemplateBaseRevision="c1899de289a04d12100db370d81485cdf75e47ca", thinking,templateMode=thinking?"Official single-user enable_thinking=True render":"Official single-user enable_thinking=False render", diagnosticHead=Environment.GetEnvironmentVariable("GITHUB_SHA"),diagnosticRun=Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),wallClockIncludesModelLoad=true});
if(Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT") is "qwen06-dual" or "qwen06-plain-transfer") return await DualPurposeDiagnostics.RunAsync(inputs,model,output,Probe);
if(Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT")=="qwen06-evidence") return await SelectionEvidenceDiagnostics.RunAsync(inputs,model,output,Probe);
var cases=new List<Case>();
foreach(var name in new[]{"cross-script","same-title-version","uncertain-abstention"}) {
 using var source=JsonDocument.Parse(File.ReadAllText(Path.Combine(inputs,"evidence/selection-probe",name+".json")));
 var root=source.RootElement; var q=root.GetProperty("Query").Deserialize<LyricsQuery>()!;
 var c=root.GetProperty("candidates").EnumerateArray().Select(x=>new Candidate(x.GetProperty("Id").GetString()!,x.GetProperty("Match").Deserialize<LyricsMatchInfo>()!)).ToArray();
 var expected=root.GetProperty("expectedId").ValueKind==JsonValueKind.Null ? null:root.GetProperty("expectedId").GetString();
 cases.Add(new(name+"-original",q,c,expected));cases.Add(new(name+"-reversed",q,c.Reverse().ToArray(),expected));
}
// New boundaries are declared before inference; annotations are never rendered into model input.
var originalVersion=cases.Single(x=>x.Name=="same-title-version-original");
cases.Add(originalVersion with {Name="wrong-versions-only",Candidates=originalVersion.Candidates.Where(x=>x.Id!=originalVersion.Expected).ToArray(),Expected=null});
var optional=originalVersion.Candidates.Single(x=>x.Id==originalVersion.Expected);
cases.Add(originalVersion with {Name="optional-metadata-unknown",Query=originalVersion.Query with {Album="",Duration=TimeSpan.Zero},Candidates=[new("c8",optional.Match with {Album="",DurationSeconds=0})],Expected="c8"});
if(thinking) cases=cases.Where(x=>x.Name is "cross-script-reversed" or "uncertain-abstention-original" or "uncertain-abstention-reversed" or "wrong-versions-only").ToList();
if(Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT")=="qwen06-admission") {
 var q=new LyricsQuery("唯一","Wang Leehom","",TimeSpan.FromSeconds(250),"diagnostic:admitted-cross-script") {CollectSelectionCandidates=true};
 var c=new[]{new Candidate("c9",new LyricsMatchInfo("唯一","告五人","",250,0)),new Candidate("c10",new LyricsMatchInfo("唯一","王力宏","",250,0))};
 var admission=c.Select(x=>new{x.Id,strictScore=LyricsMatcher.Score(q,x.Match.Title,x.Match.Artist,x.Match.Album,x.Match.DurationSeconds),candidateScore=LyricsMatcher.CandidateScore(q,x.Match.Title,x.Match.Artist,x.Match.Album,x.Match.DurationSeconds)}).ToArray();
 if(admission.Any(x=>x.strictScore>=4 || x.candidateScore<4)) throw new InvalidDataException("Declared weak-candidate boundary does not match actual production admission.");
 Save("admission",new{fixtureOnly=true,note="Duration is a deliberately equal synthetic ambiguity, not a captured recording duration.",query=q,admission});
 cases=[new("admitted-cross-script-original",q,c,"c10"),new("admitted-cross-script-reversed",q,c.Reverse().ToArray(),"c10")];
}
var inferenceBlocked=false;var results=new List<object>();
foreach(var item in cases) {
 if(inferenceBlocked) throw new InvalidOperationException("Previous owned cleanup unresolved; no overlapping inference.");
 CpuInferenceMemoryPolicy.EnsureAvailable(WindowsInferenceProcess.MaximumMemoryBytes,CpuInferenceMemoryPolicy.ReadWindowsSnapshot);
 var content=BuildPrompt(item.Query,item.Candidates);
 var prompt="<|im_start|>user\n"+content+"<|im_end|>\n<|im_start|>assistant\n"+(thinking?"":"<think>\n\n</think>\n\n");
 var promptPath=Path.Combine(output,item.Name+".prompt.txt");File.WriteAllText(promptPath,prompt,new UTF8Encoding(false));
 var arguments=new[]{"-m",model,"-f",promptPath,"--offline","--perf","--no-escape","--no-context-shift","--repeat-penalty","1.0","--no-conversation","--no-display-prompt","--simple-io","--no-warmup","-ngl","0","-c","2048","-t","4","-n",thinking?"256":"32","--temp",thinking?"0.6":"0.7","--top-p",thinking?"0.95":"0.8","--top-k","20","--min-p","0","--seed","42"};
 var native=await Probe(item.Name,exe,arguments,60,4096);
 var raw=File.ReadAllText(Path.Combine(output,item.Name+".stdout.txt"));var answer=RemoveTerminator(raw);
 string? reasoning=null;var reasoningComplete=!thinking;
 if(thinking && answer.StartsWith("<think>",StringComparison.Ordinal) && answer.IndexOf("</think>",StringComparison.Ordinal) is var boundary && boundary>=7) {reasoning=answer[7..boundary];answer=answer[(boundary+8)..].Trim();reasoningComplete=true;}
 var syntaxValid=answer=="NONE" || item.Candidates.Any(x=>x.Id==answer);
 var complete=reasoningComplete && native.ExitCode==0 && native.Reason==null && native.CleanupCompleted && raw.TrimEnd().EndsWith("[end of text]",StringComparison.Ordinal);
 var actual=answer=="NONE"?null:answer;
 var result=new {item.Name,item.Query,candidates=item.Candidates,candidateOrder=item.Candidates.Select(x=>x.Id),promptSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))),raw,reasoning,reasoningComplete,answer,syntaxValid,complete,expectedId=item.Expected,actualId=actual,correct=complete&&syntaxValid&&actual==item.Expected,native};
 Save(item.Name,result);results.Add(result);
}
Save("summary",new {diagnosticOnly=true,semanticApproved=false,results});
return 0;

static string BuildPrompt(LyricsQuery query,IReadOnlyList<Candidate> candidates) {
 static string Text(string value)=>string.IsNullOrWhiteSpace(value)?"未知":value;
 static string Duration(double value)=>value>0&&double.IsFinite(value)?value.ToString(System.Globalization.CultureInfo.InvariantCulture):"未知";
 var prompt=new StringBuilder("请根据歌曲身份选择一个匹配候选。比较标题、歌手和录音版本；同一标题但不同歌手不匹配，现场、混音与录音室版本不匹配。\n专辑和时长是补充信息，可以未知；只要已知标题、歌手和版本能唯一匹配，不必因为缺少专辑或时长而弃权。若身份不足、没有匹配或不能唯一确定，回答 NONE。\n只回答一个候选编号或 NONE，不翻译、不解释、不复制候选信息。\n");
 prompt.AppendLine($"目标：标题={Text(query.Title)}；歌手={Text(query.Artist)}；专辑={Text(query.Album)}；时长秒={Duration(query.Duration.TotalSeconds)}");
 foreach(var candidate in candidates){var match=candidate.Match;prompt.AppendLine($"{candidate.Id}：标题={Text(match.Title)}；歌手={Text(match.Artist)}；专辑={Text(match.Album)}；时长秒={Duration(match.DurationSeconds)}");}
 return prompt.Append("匹配编号：").ToString();
}
async Task<NativeResult> Probe(string label,string exe,IReadOnlyList<string> arguments,int seconds,int stdoutLimit=65_536,CancellationToken externalToken=default)
{
    var clock=Stopwatch.StartNew();long peakRss=0,peakPrivate=0;string? reason=null;int? exit=null;int? pid=null;
    bool cleanupCompleted=true; string? cleanupError=null; double cleanupSeconds=0;
    double sampledCpuSeconds=0; long firstStdoutTicks=-1; string? cpuTelemetryError=null;
    var start=new ProcessStartInfo(Path.GetFullPath(exe)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
    foreach(var a in arguments)start.ArgumentList.Add(a);
    foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("LLAMA_",StringComparison.OrdinalIgnoreCase)||k.StartsWith("GGML_",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
    start.Environment["OMP_NUM_THREADS"]="4";start.Environment["OMP_THREAD_LIMIT"]="4";
    await File.WriteAllTextAsync(Path.Combine(output,label+".arguments.json"),JsonSerializer.Serialize(new{exe,arguments,jobMemoryBytes=(long)3072*1024*1024},json));
    try
    {
        // Direct native launch through the exact production job/handle policy. No wrapper child and no unrestricted fallback.
        // CompleteAsync owns disposal, including after a bounded caller timeout. Never wrap in using.
        var child=LocalInferenceProcess.Start(start,(long)3072*1024*1024);
        var process=child.Process;pid=process.Id;
        void SampleCpu()
        {
            try { sampledCpuSeconds=Math.Max(sampledCpuSeconds,process.TotalProcessorTime.TotalSeconds); }
            catch(Exception e) when(e is InvalidOperationException or System.ComponentModel.Win32Exception) { cpuTelemetryError=e.GetType().Name; }
        }
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        void Stop(string why)
        {
            Interlocked.CompareExchange(ref reason,why,null);
            // Cancellation breaks the polling/wait even when native Kill cannot be observed.
            try { deadline.Cancel(); } catch(ObjectDisposedException) { /* Late bounded-capture callback after unresolved cleanup. */ }
        }
        var stdout=Capture(child.StandardOutput,Path.Combine(output,label+".stdout.txt"),stdoutLimit,()=>Stop("stdout-budget"),()=>Interlocked.CompareExchange(ref firstStdoutTicks,clock.ElapsedTicks,-1));
        var stderr=Capture(child.StandardError,Path.Combine(output,label+".stderr.txt"),1_048_576,()=>Stop("stderr-budget"));
        try
        {
            while(!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try{process.Refresh();SampleCpu();peakRss=Math.Max(peakRss,process.WorkingSet64);peakPrivate=Math.Max(peakPrivate,process.PrivateMemorySize64);if(peakRss>(long)3072*1024*1024)Stop("rss-budget");}
                catch(InvalidOperationException) when(process.HasExited){}
                catch(System.ComponentModel.Win32Exception) when(process.HasExited){}
                await Task.Delay(50,deadline.Token);
            }
            await process.WaitForExitAsync(deadline.Token);exit=process.ExitCode;
            SampleCpu();
            await Task.WhenAll(stdout,stderr).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch(OperationCanceledException) { reason??=externalToken.IsCancellationRequested?"caller-cancelled":"deadline"; }
        catch(Exception e) when(e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            reason??="probe-error: "+e.GetType().Name+": "+e.Message;
            await File.WriteAllTextAsync(Path.Combine(output,label+".exception.txt"),e.ToString());
        }
        finally
        {
            var cleanupClock=Stopwatch.StartNew();
            var cleanup=child.CompleteAsync(stdout,stderr);
            try { await LocalInferenceProcess.WaitForCleanupAsync(cleanup,pid.Value); }
            catch(Exception e)
            {
                cleanupCompleted=false; inferenceBlocked=true; cleanupError=e.ToString();
                reason??="cleanup-unconfirmed";
                try { await File.WriteAllTextAsync(Path.Combine(output,label+".cleanup-error.txt"),cleanupError); }
                catch { /* Structured result below still contains the cleanup error. */ }
            }
            cleanupSeconds=cleanupClock.Elapsed.TotalSeconds;
        }
    }
    catch(Exception e) when(e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
    {
        reason??="launch-or-capture-error: "+e.GetType().Name+": "+e.Message;
        await File.WriteAllTextAsync(Path.Combine(output,label+".exception.txt"),e.ToString());
    }
    // Ensure files exist for launch errors as well as completed calls.
    foreach(var suffix in new[]{".stdout.txt",".stderr.txt"}){var p=Path.Combine(output,label+suffix);if(!File.Exists(p))await File.WriteAllTextAsync(p,"");}
    var result=new NativeResult(clock.Elapsed.TotalSeconds,exit,exit.HasValue?unchecked((uint)exit.Value).ToString("X8"):null,reason,peakRss,peakPrivate,peakRss>1536L*1024*1024,pid,seconds,cleanupSeconds,cleanupCompleted,cleanupError,sampledCpuSeconds,firstStdoutTicks<0?null:(double)firstStdoutTicks/Stopwatch.Frequency,cpuTelemetryError);
    await File.WriteAllTextAsync(Path.Combine(output,label+".native.json"),JsonSerializer.Serialize(result,json));
    Console.WriteLine($"{"qwen3-06-q8"} {label}: exit={exit}, reason={reason??"none"}, {clock.Elapsed.TotalSeconds:F3}s, peakRSS={peakRss}");return result;
}
static async Task Capture(StreamReader reader,string path,int limit,Action stop,Action? onFirstData=null)
{
    await using var output=new StreamWriter(path,false,new UTF8Encoding(false));var buffer=new char[2048];int total=0,count;
    while((count=await reader.ReadAsync(buffer))>0){onFirstData?.Invoke();onFirstData=null;total+=count;await output.WriteAsync(buffer.AsMemory(0,count));await output.FlushAsync();if(total>limit){stop();return;}}
}
static string RemoveTerminator(string output){var t=output.Trim();const string marker="[end of text]";return t.EndsWith(marker,StringComparison.Ordinal)?t[..^marker.Length].TrimEnd():t;}
static async Task CheckHash(string path,string expected){if(expected.Length!=64||!expected.All(Uri.IsHexDigit))throw new InvalidDataException("Expected SHA256 is invalid.");await using var file=File.OpenRead(path);var actual=Convert.ToHexStringLower(await SHA256.HashDataAsync(file));if(!string.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("SHA256 mismatch: "+path);}
record NativeResult(double Seconds,int? ExitCode,string? ExitCodeHex,string? Reason,long PeakRssBytes,long PeakPrivateBytes,bool ExceedsCompactRssBaseline,int? ProcessId,int InferenceBudgetSeconds,double CleanupSeconds,bool CleanupCompleted,string? CleanupError,double SampledCpuSeconds,double? FirstStdoutObservedSeconds,string? CpuTelemetryError);

record Candidate(string Id,LyricsMatchInfo Match);
record Case(string Name,LyricsQuery Query,Candidate[] Candidates,string? Expected);
