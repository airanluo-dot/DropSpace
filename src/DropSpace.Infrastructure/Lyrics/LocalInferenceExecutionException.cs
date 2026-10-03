namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Contains only fixed stage names and an exit code, never source text or personal paths.</summary>
public sealed class LocalInferenceExecutionException(int exitCode, bool tokenizer)
    : IOException($"Local {(tokenizer ? "tokenizer" : "inference")} exited with code {exitCode}.")
{
    public int ExitCode { get; } = exitCode;
}
