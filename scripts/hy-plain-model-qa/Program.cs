using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

if(args.Length==1 && args[0]=="--self-test") { PlainProtocol.SelfTest(); return; }
if(!OperatingSystem.IsWindows() || IntPtr.Size!=8) throw new PlatformNotSupportedException("Windows x64 only");
if(args.Length!=1) throw new ArgumentException("Pass QA config path");
var json=new JsonSerializerOptions {PropertyNameCaseInsensitive=true,WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
var config=JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(args[0]),json)!;
if(config.PromptProfile!="official-plain-per-line-v1" || config.OutputSchema!="host-mapped-id-text-v1" || config.EvaluationMode!="plain-control" || config.ModelId!="hy-mt2-18-q8" || config.MemoryMiB!=3072) throw new InvalidDataException("Unexpected profile");
Directory.CreateDirectory(config.Output);
await CheckHash(config.Model,config.ModelSha256);await CheckHash(config.Executable,config.ExecutableSha256);await CheckHash(config.Tokenizer,config.TokenizerSha256);
if(new FileInfo(config.Model).Length!=config.ModelBytes) throw new InvalidDataException("Model bytes mismatch");
await CheckHash(config.Source,"63266d20dbcd7ce93249f1b1a808a56317fa981c1bf3146a9750ffc53bc26549");
await CheckHash(config.HoldoutSource,"88a30d2ddd52307ab5832b457400c4a50545800147ac6c9e8863dd71f96f8b56");
var source=JsonSerializer.Deserialize<LyricsDocument>(await File.ReadAllTextAsync(config.Source),json)!;
var holdout=JsonSerializer.Deserialize<LyricsDocument>(await File.ReadAllTextAsync(config.HoldoutSource),json)!;
var results=new List<object>();bool anyFailure=false,inferenceBlocked=false;int calls=0;
async Task Save()=>await File.WriteAllTextAsync(Path.Combine(config.Output,"results.json"),JsonSerializer.Serialize(results,json));
await RunCase("screen-en",source,[38,40,41,42,44,45],"en-US",false);
await RunCase("screen-zh",source,[14,16,26,29,32,33],"zh-CN",false);
await RunCase("holdout-en",holdout,Enumerable.Range(0,12).ToArray(),"en-US",true);
await RunCase("holdout-zh",holdout,Enumerable.Range(0,12).ToArray(),"zh-CN",true);
results.Add(new{phase="summary",nativeTranslationRequests=calls,expectedRequests=30,semanticStatus="PENDING SEMANTIC REVIEW",qualityApproved=false});await Save();
Environment.ExitCode=anyFailure || calls!=30?2:0;

async Task RunCase(string label,LyricsDocument document,int[] ids,string target,bool isHoldout)
{
    using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(180));
    var clock=Stopwatch.StartNew();var mapped=new List<LineOutput>();bool valid=true;
    foreach(var id in ids)
    {
        if(inferenceBlocked || budget.IsCancellationRequested) {valid=false;break;}
        string language=isHoldout?new[]{"en-US","zh-CN","ja","ko"}[id/3]:new[]{"en-US","ja","ko","zh-CN"}[id/12];
        if(language==target) {mapped.Add(new(id,document.Lines[id].Text));results.Add(new{phase="same-target-bypass",label,id,language});await Save();continue;}
        var prompt=PlainProtocol.Prompt(document.Lines[id].Text,target);
        var name=label+"-"+id;var path=Path.Combine(config.Output,name+".prompt.txt");
        await File.WriteAllTextAsync(path,prompt,new UTF8Encoding(false));
        calls++;
        var native=await Probe(name,config.Executable,CompletionArgs(path,2048),60,externalToken:budget.Token);
        var raw=await File.ReadAllTextAsync(Path.Combine(config.Output,name+".stdout.txt"));
        var text=RemoveTerminator(raw);
        var lineValid=native.ExitCode==0 && native.Reason is null && native.CleanupCompleted && PlainProtocol.ValidLine(text,document.Lines[id].Text);
        if(lineValid) mapped.Add(new(id,text));else valid=false;
        results.Add(new{phase="line",label,id,sourceLanguage=language,target,promptSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))),valid=lineValid,native,semanticStatus="PENDING SEMANTIC REVIEW"});await Save();
        if(budget.IsCancellationRequested) break;
    }
    var hostJson=JsonSerializer.Serialize(mapped,json);await File.WriteAllTextAsync(Path.Combine(config.Output,label+".host-mapped.json"),hostJson);
    valid &= mapped.Count==ids.Length && clock.Elapsed.TotalSeconds<=180;
    if(valid) valid=LyricsTranslationOutput.TryApply(hostJson,document,ids,target,out var translated) && translated!.Lines.Select(x=>(x.Text,x.Start,x.End)).SequenceEqual(document.Lines.Select(x=>(x.Text,x.Start,x.End)));
    anyFailure|=!valid;results.Add(new{phase="case",label,valid,seconds=clock.Elapsed.TotalSeconds,semanticStatus="PENDING SEMANTIC REVIEW",routing="Supplied fixture language tags; no automatic detection",alignment="Host binds each complete response to its request ID"});await Save();
}

List<string> CompletionArgs(string promptPath,int outputTokens)
{
    var a=new List<string>{"-m",Path.GetFullPath(config.Model),"-f",promptPath,"--offline","--perf","--no-escape","--jinja","--single-turn","--load-mode","none","--no-display-prompt","--simple-io","--no-context-shift","--reasoning","off","-t","4","-tb","4","-ngl","0","-c","4096","-n",outputTokens.ToString(),"--seed","42","--temp","0.1","--top-k","20","--top-p","0.8","--min-p","0.05","--repeat-penalty","1.0","--frequency-penalty","0","--presence-penalty","0"};
    return a;
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
    await File.WriteAllTextAsync(Path.Combine(config.Output,label+".arguments.json"),JsonSerializer.Serialize(new{exe,arguments,jobMemoryBytes=(long)config.MemoryMiB*1024*1024},json));
    try
    {
        // Direct native launch through the exact production job/handle policy. No wrapper child and no unrestricted fallback.
        // CompleteAsync owns disposal, including after a bounded caller timeout. Never wrap in using.
        var child=LocalInferenceProcess.Start(start,(long)config.MemoryMiB*1024*1024);
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
        var stdout=Capture(child.StandardOutput,Path.Combine(config.Output,label+".stdout.txt"),stdoutLimit,()=>Stop("stdout-budget"),()=>Interlocked.CompareExchange(ref firstStdoutTicks,clock.ElapsedTicks,-1));
        var stderr=Capture(child.StandardError,Path.Combine(config.Output,label+".stderr.txt"),1_048_576,()=>Stop("stderr-budget"));
        try
        {
            while(!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try{process.Refresh();SampleCpu();peakRss=Math.Max(peakRss,process.WorkingSet64);peakPrivate=Math.Max(peakPrivate,process.PrivateMemorySize64);if(peakRss>(long)config.MemoryMiB*1024*1024)Stop("rss-budget");}
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
            await File.WriteAllTextAsync(Path.Combine(config.Output,label+".exception.txt"),e.ToString());
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
                try { await File.WriteAllTextAsync(Path.Combine(config.Output,label+".cleanup-error.txt"),cleanupError); }
                catch { /* Structured result below still contains the cleanup error. */ }
            }
            cleanupSeconds=cleanupClock.Elapsed.TotalSeconds;
        }
    }
    catch(Exception e) when(e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
    {
        reason??="launch-or-capture-error: "+e.GetType().Name+": "+e.Message;
        await File.WriteAllTextAsync(Path.Combine(config.Output,label+".exception.txt"),e.ToString());
    }
    // Ensure files exist for launch errors as well as completed calls.
    foreach(var suffix in new[]{".stdout.txt",".stderr.txt"}){var p=Path.Combine(config.Output,label+suffix);if(!File.Exists(p))await File.WriteAllTextAsync(p,"");}
    var result=new NativeResult(clock.Elapsed.TotalSeconds,exit,exit.HasValue?unchecked((uint)exit.Value).ToString("X8"):null,reason,peakRss,peakPrivate,peakRss>1536L*1024*1024,pid,seconds,cleanupSeconds,cleanupCompleted,cleanupError,sampledCpuSeconds,firstStdoutTicks<0?null:(double)firstStdoutTicks/Stopwatch.Frequency,cpuTelemetryError);
    await File.WriteAllTextAsync(Path.Combine(config.Output,label+".native.json"),JsonSerializer.Serialize(result,json));
    Console.WriteLine($"{config.ModelId} {label}: exit={exit}, reason={reason??"none"}, {clock.Elapsed.TotalSeconds:F3}s, peakRSS={peakRss}");return result;
}
static async Task Capture(StreamReader reader,string path,int limit,Action stop,Action? onFirstData=null)
{
    await using var output=new StreamWriter(path,false,new UTF8Encoding(false));var buffer=new char[2048];int total=0,count;
    while((count=await reader.ReadAsync(buffer))>0){onFirstData?.Invoke();onFirstData=null;total+=count;await output.WriteAsync(buffer.AsMemory(0,count));await output.FlushAsync();if(total>limit){stop();return;}}
}
static string RemoveTerminator(string output){var t=output.Trim();const string marker="[end of text]";return t.EndsWith(marker,StringComparison.Ordinal)?t[..^marker.Length].TrimEnd():t;}
static async Task CheckHash(string path,string expected){if(expected.Length!=64||!expected.All(Uri.IsHexDigit))throw new InvalidDataException("Expected SHA256 is invalid.");await using var file=File.OpenRead(path);var actual=Convert.ToHexStringLower(await SHA256.HashDataAsync(file));if(!string.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("SHA256 mismatch: "+path);}
record Config(string ModelId,string Model,long ModelBytes,string ModelSha256,string Executable,string ExecutableSha256,string Tokenizer,string TokenizerSha256,string Source,string Output,int MemoryMiB,string OutputSchema,string PromptProfile,string EvaluationMode,string HoldoutSource,string HoldoutSha256);
record NativeResult(double Seconds,int? ExitCode,string? ExitCodeHex,string? Reason,long PeakRssBytes,long PeakPrivateBytes,bool ExceedsCompactRssBaseline,int? ProcessId,int InferenceBudgetSeconds,double CleanupSeconds,bool CleanupCompleted,string? CleanupError,double SampledCpuSeconds,double? FirstStdoutObservedSeconds,string? CpuTelemetryError);

record LineOutput(int id,string text);
