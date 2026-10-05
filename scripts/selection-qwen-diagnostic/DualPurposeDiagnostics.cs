using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

// Diagnostic only; both production translation prompts remain unchanged.
internal static class DualPurposeDiagnostics
{
    internal static async Task<int> RunAsync(string inputs,string qwen,string output,
        Func<string,string,IReadOnlyList<string>,int,int,CancellationToken,Task<NativeResult>> probe)
    {
        var json=new JsonSerializerOptions{WriteIndented=true};
        void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,json));
        using var total=new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var cases=new[]{
            new TranslationCase("to-zh","zh-CN",Document([
                "The bird beat its wings against the locked cage.", "I let it go.",
                "I didn't say I never loved you.", "君が帰らなくても、私は窓を閉めない。"])),
            new TranslationCase("to-en","en-US",Document([
                "屋里只剩我和她，你抱着一盏灯。", "别把灯递给我，给她；我会在门外等。",
                "我只归还了三封信，没有拿走第四封。", "月光落进空杯，却没有替我说一句话。"])),
        };
        Save("dual-contract",new{diagnosticOnly=true,authorizationUtc="2026-10-05T01:30:22Z",authorizationSentinel="4279b83aa968819192c15d5f5516c3ab",optionalAdditionOnly=true,productionChanged=false,
            source="Original synthetic QA and existing holdout, not provider captures",cases,
            qwenProtocol=LyricsTranslationPrompt.Version,hyProtocol=PlainHyLyricsProtocol.Version,
            comparisonLimit="Qwen contextual batch JSON vs actual Hy per-line protocol; context/protocol differ, not an isolated weights benchmark",semanticApproved=false});
        if(Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT")=="qwen06-plain-transfer") {
            var plain=new List<object>();
            foreach(var sample in new[]{(Item:cases[0],Index:2),(Item:cases[0],Index:3),(Item:cases[1],Index:1)}) {
                var item=sample.Item;var index=sample.Index;
                var prompt=PlainHyLyricsProtocol.BuildPrompt(item.Source.Lines[index].Text,item.Target);
                var label="qwen-plain-"+item.Name+"-"+index;var path=Path.Combine(output,label+".prompt.txt");
                File.WriteAllText(path,Render(prompt),new UTF8Encoding(false));
                var native=await probe(label,Path.Combine(inputs,"runtime/llama-completion-avx2.exe"),Arguments(qwen,path,256,null),60,16384,total.Token);
                if(!native.CleanupCompleted)throw new InvalidOperationException("Plain transfer owner cleanup unresolved.");
                var raw=File.ReadAllText(Path.Combine(output,label+".stdout.txt"));var text=LlamaCompletionRunner.RemoveRuntimeTerminator(raw);
                var complete=native.ExitCode==0&&native.Reason is null&&raw.TrimEnd().EndsWith("[end of text]",StringComparison.Ordinal);
                var valid=complete&&PlainHyLyricsProtocol.IsCompleteLine(text);
                var mapped=item.Source with {Lines=item.Source.Lines.Select((line,i)=>i==index&&valid?line with {Secondary=text,TranslationOrigin=LyricsTranslationOrigin.LocalAi,TranslationLanguage=item.Target}:line).ToArray()};
                var result=new{diagnosticOnly=true,model="Qwen3-0.6B-Q8",protocol=PlainHyLyricsProtocol.Version,item.Name,item.Target,index,source=item.Source.Lines[index].Text,raw,text,complete,structurallyValid=valid,mapped,native,semanticApproved=false};
                Save(label,result);plain.Add(result);
            }
            Save("plain-transfer-summary",new{diagnosticOnly=true,semanticApproved=false,observations=plain});return 0;
        }
        using var packages=new AiModelPackageService(Path.Combine(output,"hy-diagnostic-model"));
        var descriptor=AiLyricsModelCatalog.ExperimentalPlain;
        var hy=await packages.DownloadAsync(descriptor.Id,true,null,total.Token);
        using var modelLease=new FileStream(hy,FileMode.Open,FileAccess.Read,FileShare.Read);
        var runtime=new AiLyricsRuntimePackage(Assembly.GetExecutingAssembly(),Path.Combine(output,"hy-runtime"));
        if(runtime.GetManifestCacheIdentity()!="26162299cd270bf63f25181e960b3139cfe20421eecdc9da44cea99dfb1711ba")throw new InvalidDataException("Original runtime manifest mismatch.");
        var runner=new PersistentPlainLyricsRunner(runtime,new(){GpuEnabled=false});
        var observations=new List<object>();
        try {
            foreach(var item in cases)foreach(var index in new[]{1,2,3}) {
                var prompt=PlainHyLyricsProtocol.BuildPrompt(item.Source.Lines[index].Text,item.Target);
                var label="hy-"+item.Name+"-"+index;File.WriteAllText(Path.Combine(output,label+".prompt.txt"),prompt);
                var clock=Stopwatch.StartNew();
                var text=await runner.RunPlainAsync("",hy,prompt,output,total.Token,descriptor.Sha256);
                var result=new{model=descriptor.Id,descriptor.Bytes,descriptor.Sha256,item.Name,item.Target,index,source=item.Source.Lines[index].Text,text,structurallyValid=PlainHyLyricsProtocol.IsCompleteLine(text),milliseconds=clock.Elapsed.TotalMilliseconds,backend=runner.LastExecutionBackend,semanticApproved=false};
                Save(label,result);observations.Add(result);
            }
        }
        finally {await runner.DrainCleanupAsync(CancellationToken.None);runner.Dispose();Save("hy-cleanup",new{cleanupCompleted=true,method="Actual production runner DrainCleanupAsync confirmed before Qwen starts"});}
        var exe=Path.Combine(inputs,"runtime/llama-completion-avx2.exe");
        var requests=new List<(string Prompt,int[] Ids)>();
        foreach(var item in cases) {
            var ids=new[]{1,2,3};
            var prompt=LyricsTranslationPrompt.Build(new("Original dual-use diagnostic","QA","",TimeSpan.FromSeconds(16)),item.Source,ids,item.Target);
            var label="qwen-"+item.Name;var path=Path.Combine(output,label+".prompt.txt");
            File.WriteAllText(path,Render(prompt),new UTF8Encoding(false));
            var native=await probe(label,exe,Arguments(qwen,path,768,LyricsTranslationPrompt.OutputSchema(ids)),90,65536,total.Token);
            if(!native.CleanupCompleted)throw new InvalidOperationException("Qwen cleanup unresolved; further inference blocked.");
            var raw=File.ReadAllText(Path.Combine(output,label+".stdout.txt"));var text=LlamaCompletionRunner.RemoveRuntimeTerminator(raw);
            var complete=native.ExitCode==0&&native.Reason is null&&raw.TrimEnd().EndsWith("[end of text]",StringComparison.Ordinal);
            var valid=LyricsTranslationOutput.TryApply(text,item.Source,ids,item.Target,out var mapped);
            var preserved=mapped.Lines.Select((line,i)=>line.Text==item.Source.Lines[i].Text&&line.Start==item.Source.Lines[i].Start&&line.End==item.Source.Lines[i].End).All(x=>x);
            var result=new{model="Qwen3-0.6B-Q8",item.Name,item.Target,ids,raw,text,complete,structurallyValid=complete&&valid,timestampsAndOriginalPreserved=valid&&preserved,mapped,native,semanticApproved=false};
            Save(label,result);observations.Add(result);requests.Add((path,ids));
        }
        // Explicit diagnostic topology, not a claim that production Qwen sharing is implemented.
        await LocalInferenceProcess.InferenceGate.WaitAsync(total.Token);
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var waiterCancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        bool waiterCancelled=false;int queuedNativeCalls=0;
        async Task Waiter() {try{await LocalInferenceProcess.InferenceGate.WaitAsync(waiterCancel.Token);try{queuedNativeCalls++;}finally{LocalInferenceProcess.InferenceGate.Release();}}catch(OperationCanceledException){waiterCancelled=true;}}
        var waiter=Waiter();NativeResult cancelled;
        try {cancelled=await probe("qwen-cancelled-translation",exe,Arguments(qwen,requests[0].Prompt,768,LyricsTranslationPrompt.OutputSchema(requests[0].Ids)),90,65536,cancel.Token);}
        finally {LocalInferenceProcess.InferenceGate.Release();}
        await waiter;
        if(!cancelled.CleanupCompleted)throw new InvalidOperationException("Canceled owner cleanup unresolved.");
        var recoveryPrompt="请根据歌曲身份选择匹配候选；标题和歌手必须匹配。不确定回答 NONE。只回答编号或 NONE。\n目标：唯一；Wang Leehom\nc9：唯一；告五人\nc10：唯一；王力宏\n匹配编号：";
        var recoveryPath=Path.Combine(output,"qwen-selection-after-cancel.prompt.txt");File.WriteAllText(recoveryPath,Render(recoveryPrompt),new UTF8Encoding(false));
        await LocalInferenceProcess.InferenceGate.WaitAsync(total.Token);NativeResult recovered;
        try {recovered=await probe("qwen-selection-after-cancel",exe,Arguments(qwen,recoveryPath,32,null),60,4096,total.Token);}
        finally {LocalInferenceProcess.InferenceGate.Release();}
        var recoveryRaw=File.ReadAllText(Path.Combine(output,"qwen-selection-after-cancel.stdout.txt"));
        Save("dual-lifecycle",new{diagnosticTopologyOnly=true,productionQwenIntegrationExists=false,cancelled,waiterCancelled,queuedNativeCalls,recovered,recoveryRaw});
        Save("dual-summary",new{diagnosticOnly=true,semanticApproved=false,observations});return 0;
    }
    private static LyricsDocument Document(string[] lines)=>new(lines.Select((text,i)=>new LyricsLine(TimeSpan.FromSeconds(i*4),TimeSpan.FromSeconds(i*4+4),text,null,[])).ToArray(),LyricsProviderKind.NetEase);
    private static string Render(string text)=>"<|im_start|>user\n"+text+"<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
    private static List<string> Arguments(string model,string path,int count,string? schema) {
        var result=new List<string>{"-m",model,"-f",path,"--offline","--perf","--no-escape","--no-context-shift","--no-conversation","--no-display-prompt","--simple-io","--no-warmup","-ngl","0","-c","4096","-t","4","-tb","4","-n",count.ToString(),"--temp","0.7","--top-p","0.8","--top-k","20","--min-p","0","--repeat-penalty","1","--seed","42"};
        if(schema is not null)result.AddRange(["-j",schema]);return result;
    }
    private sealed record TranslationCase(string Name,string Target,LyricsDocument Source);
}
