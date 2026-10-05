using System.Reflection;
using System.Text.Json;

// Diagnostic source only. Never enrolled into the app's production model catalog.
internal sealed record SelectionDiagnosticProfile(string Id,string File,string Revision,long Bytes,string Sha256,
    string Url,int MemoryMiB,bool EmptyThinkingBlock,string TemplateRevision,string AuthorizationReference)
{
    internal static SelectionDiagnosticProfile Resolve(string? variant)
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("DropSpace.SelectionDiagnosticProfiles.json")
            ?? throw new InvalidDataException("Pinned diagnostic profiles missing.");
        var profiles=JsonSerializer.Deserialize<SelectionDiagnosticProfile[]>(stream)!;
        var id=variant is "qwen4-evidence" or "qwen4-template-validation" or "qwen4-deterministic"?"qwen3-4b-instruct-2507-q8":"qwen3-06-q8";
        return profiles.Single(profile=>profile.Id==id);
    }
    internal long MemoryBytes=>(long)MemoryMiB*1024*1024;
    internal string Render(string content)=>"<|im_start|>user\n"+content+"<|im_end|>\n<|im_start|>assistant\n"+
        (EmptyThinkingBlock?"<think>\n\n</think>\n\n":"");
}
