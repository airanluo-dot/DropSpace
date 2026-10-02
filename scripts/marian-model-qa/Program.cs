using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("Windows x64 QA only.");
if(args.Length!=1) throw new ArgumentException("Pass the prepared QA configuration path.");
var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
var config=JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(args[0]),json)!;
if(config.MemoryMiB!=3072) throw new ArgumentException("Marian prototype keeps the original 3 GiB hard cap.");
Directory.CreateDirectory(config.Output);
await Verify(config.Source,"63266d20dbcd7ce93249f1b1a808a56317fa981c1bf3146a9750ffc53bc26549");
await Verify(config.Python,config.PythonSha256);
await Verify(config.InferScript,config.InferScriptSha256);
await Verify(config.ConversionManifest,config.ConversionManifestSha256);
await Verify(config.Holdout,"88a30d2ddd52307ab5832b457400c4a50545800147ac6c9e8863dd71f96f8b56");
var source=JsonSerializer.Deserialize<LyricsDocument>(await File.ReadAllTextAsync(config.Source),json)!;
var holdout=JsonSerializer.Deserialize<LyricsDocument>(await File.ReadAllTextAsync(config.Holdout),json)!;
var results=new List<object>();var anyFailure=false;var inferenceBlocked=false;
async Task Save()=>await File.WriteAllTextAsync(Path.Combine(config.Output,"results.json"),JsonSerializer.Serialize(results,json));
foreach(var fixture in new[]{("full48",source,12),("holdout12",holdout,3)})
    foreach(var target in new[]{"en","zh"}) await Target(fixture.Item1,fixture.Item2,fixture.Item3,target,false);
await Target("source-only",source with { Lines=source.Lines.Take(12).ToArray() },12,"en",false);
await Target("cancellation",source,12,"zh",true);
Environment.ExitCode=anyFailure?2:0;

async Task Target(string label,LyricsDocument document,int group,string target,bool cancel)
{
    label+="-"+target;
    if(inferenceBlocked){anyFailure=true;results.Add(new{label,valid=false,reason="Prior cleanup unconfirmed"});await Save();return;}
    var lines=document.Lines.Select((line,id)=>new {id,text=line.Text,language=new[]{"en","ja","ko","zh"}[id/group]}).ToArray();
    // The frozen holdout uses EN/ZH/JA/KO order; the original song uses EN/JA/KO/ZH.
    if(label.StartsWith("holdout")) lines=document.Lines.Select((line,id)=>new{id,text=line.Text,language=new[]{"en","zh","ja","ko"}[id/group]}).ToArray();
    var evidence=Path.Combine(config.Output,label);Directory.CreateDirectory(evidence);
    var requestPath=Path.Combine(evidence,"request.json");
    await File.WriteAllTextAsync(requestPath,JsonSerializer.Serialize(new{manifest=config.ConversionManifest,lines,target,evidence,sourceLanguagePolicy="Known fixture tags; automatic source detection is NOT tested"},json));
    using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(180));
    using var observerStop=new CancellationTokenSource();
    bool cancelledDuringInference=false;
    var observer=cancel?Task.Run(async()=>
    {
        try
        {
            while(!File.Exists(Path.Combine(evidence,"inference-started.json"))) await Task.Delay(25,observerStop.Token);
            cancelledDuringInference=true;budget.Cancel();
        }
        catch(OperationCanceledException) { }
    }):Task.CompletedTask;
    var run=await Probe(label,config.Python,["-I","-X","utf8","-u",config.InferScript,"--request",requestPath],180,external:budget.Token);
    observerStop.Cancel();await observer;
    bool valid=false;string? error=null;
    try
    {
        if(cancel) valid=cancelledDuringInference && run.Reason=="caller-cancelled" && run.CleanupCompleted;
        else if(run.ExitCode==0 && run.Reason is null && run.CleanupCompleted && run.Seconds<=180)
        {
            using var parsed=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(config.Output,label+".stdout.txt")));
            var outputs=parsed.RootElement.GetProperty("outputs");
            valid=true;
            foreach(var phase in new[]{"cold","warm"})
            {
                var raw=outputs.GetProperty(phase).GetRawText();
                var ids=Enumerable.Range(0,document.Lines.Count).ToArray();
                if(label.StartsWith("source-only"))
                    valid &= parsed.RootElement.GetProperty("modelCalls").GetInt32()==0 && JsonSerializer.Deserialize<LineOutput[]>(raw,json)!.Select(x=>x.text).SequenceEqual(document.Lines.Select(x=>x.Text));
                else
                {
                    var applied=LyricsTranslationOutput.TryApply(raw,document,ids,target=="en"?"en-US":"zh-CN",out var translated);
                    valid &= applied;
                    if(applied) valid &= translated!.Lines.Select(x=>(x.Text,x.Start,x.End)).SequenceEqual(document.Lines.Select(x=>(x.Text,x.Start,x.End)));
                }
            }
        }
    }
    catch(Exception e){error=e.ToString();}
    anyFailure|=!valid;
    results.Add(new {label,native=run,valid,error,semanticStatus="PENDING SEMANTIC REVIEW",routing="Known fixture source tags; no automatic language identification",warmMeaning="Second decode on retained CT2 instances, not a translation-result cache",cancellationScope=cancel?"Cancelled after helper dispatched actual asynchronous CT2 decode; job exit confirmed":null});await Save();
}

static async Task Verify(string path,string expected)
{
    await using var stream=File.OpenRead(path);
    if(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream))!=expected.ToLowerInvariant()) throw new InvalidDataException("QA file SHA256 mismatch: "+Path.GetFileName(path));
}
async Task<NativeResult> Probe(string label,string exe,IReadOnlyList<string> arguments,int seconds,int stdoutLimit=262_144,CancellationToken external=default)
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
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(external);
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
        catch(OperationCanceledException) { reason??=external.IsCancellationRequested?"caller-cancelled":"deadline"; }
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
    Console.WriteLine($"{"Marian"} {label}: exit={exit}, reason={reason??"none"}, {clock.Elapsed.TotalSeconds:F3}s, peakRSS={peakRss}");return result;
}
static async Task Capture(StreamReader reader,string path,int limit,Action stop,Action? onFirstData=null)
{
    await using var output=new StreamWriter(path,false,new UTF8Encoding(false));var buffer=new char[2048];int total=0,count;
    while((count=await reader.ReadAsync(buffer))>0){onFirstData?.Invoke();onFirstData=null;total+=count;await output.WriteAsync(buffer.AsMemory(0,count));await output.FlushAsync();if(total>limit){stop();return;}}
}

record Config(string Python,string PythonSha256,string InferScript,string InferScriptSha256,string ConversionManifest,string ConversionManifestSha256,string Source,string Holdout,string Output,int MemoryMiB);
record LineOutput(int id,string text);
record NativeResult(double Seconds,int? ExitCode,string? ExitCodeHex,string? Reason,long PeakRssBytes,long PeakPrivateBytes,bool ExceedsCompactRssBaseline,int? ProcessId,int InferenceBudgetSeconds,double CleanupSeconds,bool CleanupCompleted,string? CleanupError,double SampledCpuSeconds,double? FirstStdoutObservedSeconds,string? CpuTelemetryError);
