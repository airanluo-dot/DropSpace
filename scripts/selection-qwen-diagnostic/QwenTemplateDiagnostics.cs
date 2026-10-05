using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

// Frozen before inference. Controls vary transport only; held-out rows are never prompt hints.
internal static class QwenTemplateDiagnostics
{
    internal static async Task<int> RunAsync(string inputs,string model,string output,
        Func<string,string,IReadOnlyList<string>,int,int,CancellationToken,Task<NativeResult>> probe)
    {
        using var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("DropSpace.IdentityValidationV1.json")!;
        using var buffer=new MemoryStream();await resource.CopyToAsync(buffer);var corpus=buffer.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(output,"validation-corpus.json"),corpus);
        var fixtures=JsonSerializer.Deserialize<Corpus>(corpus)!;
        var variant=Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT");
        var profile=SelectionDiagnosticProfile.Resolve(variant);
        var binaryOnly=variant is "qwen4-deterministic" or "qwen8-evidence";
        var deterministic=Environment.GetEnvironmentVariable("DIAGNOSTIC_VARIANT")=="qwen4-deterministic";
        var json=new JsonSerializerOptions {WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
        void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,json));
        Save("validation-contract",new {diagnosticOnly=true,profile,fixtures.Version,fixtures.InstructionPolicy,
            corpusSha256=Convert.ToHexStringLower(SHA256.HashData(corpus)),deterministic,samplerChange=deterministic?"temperature only: 0.7 to 0":"none",
            actualInputBytesRecorded=true,officialEos=new[]{"<|im_end|>","<|endoftext|>"},extraRuntimeEog="</s>"});
        using var total=new CancellationTokenSource(TimeSpan.FromMinutes(12));var results=new List<object>();
        foreach(var item in fixtures.Cases)
        foreach(var reversed in item.Reverse?new[]{false,true}:new[]{false})
        foreach(var binary in item.TemplateControl&&!binaryOnly?new[]{false,true}:new[]{true})
        {
            var rows=reversed?item.Candidates.Reverse().ToArray():item.Candidates;
            var content=SelectionEvidenceDiagnostics.Build(item.Query,rows,true);
            // Literal special markers in metadata stay JSON data, never template tokens.
            // Control fixtures have no '<', so their exact prior content is unchanged.
            content=content.Replace("<","\\u003c",StringComparison.Ordinal);
            var prompt=profile.Render(content);var bytes=Encoding.UTF8.GetBytes(prompt);
            var effective=binary?bytes:bytes[..^1];
            var label=item.Name+(reversed?"-reversed":"-original")+(binary?"-binary":"-text");
            var path=Path.Combine(output,label+".prompt.txt");await File.WriteAllBytesAsync(path,bytes);
            await File.WriteAllBytesAsync(Path.Combine(output,label+".effective-input.txt"),effective);
            CpuInferenceMemoryPolicy.EnsureAvailable(profile.MemoryBytes,CpuInferenceMemoryPolicy.ReadWindowsSnapshot);
            var args=new[]{"-m",model,binary?"-bf":"-f",path,"--special","--offline","--perf","--no-escape","--no-context-shift","--no-conversation","--no-display-prompt","--simple-io","--no-warmup","-ngl","0","-c","2048","-t","4","-tb","4","-n","32","--temp",deterministic?"0":"0.7","--top-p","0.8","--top-k","20","--min-p","0","--repeat-penalty","1","--seed","42"};
            var native=await probe(label,Path.Combine(inputs,"runtime/llama-completion-avx2.exe"),args,60,4096,total.Token);
            if(!native.CleanupCompleted)throw new InvalidOperationException("Cleanup unresolved; following inference blocked.");
            var raw=await File.ReadAllTextAsync(Path.Combine(output,label+".stdout.txt"));
            var text=LlamaCompletionRunner.RemoveRuntimeTerminator(raw).TrimEnd();string? eos=null;
            foreach(var marker in new[]{"<|im_end|>","<|endoftext|>","</s>"})
                if(text.EndsWith(marker,StringComparison.Ordinal)){eos=marker;text=text[..^marker.Length].Trim();break;}
            var official=eos is "<|im_end|>" or "<|endoftext|>";
            var complete=native.ExitCode==0&&native.Reason is null&&raw.TrimEnd().EndsWith("[end of text]",StringComparison.Ordinal)&&official;
            var syntax=text=="NONE"||rows.Any(row=>row.Id==text);var actual=text=="NONE"?null:text;
            var result=new {item.Name,item.TemplateControl,reversed,binary,
                candidateOrder=rows.Select(row=>row.Id),item.Query,rows,
                admission=rows.Select(row=>new{row.Id,strict=LyricsMatcher.Score(item.Query,row.Match.Title,row.Match.Artist,row.Match.Album,row.Match.DurationSeconds),
                    collected=LyricsMatcher.CandidateScore(item.Query,row.Match.Title,row.Match.Artist,row.Match.Album,row.Match.DurationSeconds)}),
                fileSha256=Convert.ToHexStringLower(SHA256.HashData(bytes)),effectiveInputSha256=Convert.ToHexStringLower(SHA256.HashData(effective)),
                raw,eos,officialEos=official,answer=text,complete,syntaxValid=syntax,expectedId=item.Expected,actualId=actual,
                correct=complete&&syntax&&actual==item.Expected,unsupportedAcceptance=item.Expected is null&&syntax&&actual is not null,native};
            Save(label,result);results.Add(result);
        }
        Save("validation-summary",new {diagnosticOnly=true,semanticApproved=false,results});return 0;
    }
    private sealed record Corpus(string Version,string InstructionPolicy,Fixture[] Cases);
    private sealed record Fixture(string Name,LyricsQuery Query,Candidate[] Candidates,string? Expected,bool Reverse,bool TemplateControl);
}
