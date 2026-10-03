namespace DropSpace.Core.Lyrics;

public sealed record AiLyricsModelDescriptor(string Id, string Name, Uri DownloadUri, long Bytes, string Sha256);

public static class AiLyricsModelCatalog
{
    public static AiLyricsModelDescriptor Standard { get; } = new(
        "hy-mt2-standard", "Hy-MT2 1.8B Q4_K_M",
        new Uri("https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q4_K_M.gguf"),
        1_133_080_448,
        "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699");

    // Third-party Apache-2.0 quantization. Same parameter count; lower download/RAM,
    // not a promise of faster inference. Only this pinned file gets the EOS correction.
    public static AiLyricsModelDescriptor Compact { get; } = new(
        "hy-mt2-lightweight", "Hy-MT2 1.8B IQ3_S (experimental)",
        new Uri("https://huggingface.co/mradermacher/Hy-MT2-1.8B-i1-GGUF/resolve/9f5c7d98d8b625800775e6197e55c7ed38f2f33a/Hy-MT2-1.8B.i1-IQ3_S.gguf"),
        876_311_552,
        "46e67068820c68ac43ea9f304556c641e8c9dbf54efb14cef2b4dd540a30f12a");

    // Explicit opt-in Beta: frozen Tencent Q8 bytes evaluated with the official plaintext
    // per-line template. This does not promise real-time generation or semantic correctness.
    public static AiLyricsModelDescriptor ExperimentalPlain { get; } = new(
        "hy-mt2-18-q8-plain-beta", "Hy-MT2 1.8B Q8_0 (Beta)",
        new Uri("https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q8_0.gguf"),
        1_908_528_192,
        "5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4");

    // Optional larger profile. Metadata and tensor types are verified at the pinned revision;
    // this is not a claim of measured lyric quality, latency or compatibility with every GPU.
    public static AiLyricsModelDescriptor ExperimentalLargePlain { get; } = new(
        "hy-mt2-7b-q8-plain-beta", "Hy-MT2 7B Q8_0 (Beta)",
        new Uri("https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/ab8472660ac61fac25f1af43fac2599d52a8a775/HY-MT2-7B-Q8_0.gguf"),
        7_981_928_896,
        "58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0");

    public static IReadOnlyList<AiLyricsModelDescriptor> All { get; } = Array.AsReadOnly(new[] { ExperimentalPlain, ExperimentalLargePlain });

    public static IReadOnlyList<AiLyricsModelDescriptor> Legacy { get; } = Array.AsReadOnly(new[] { Standard, Compact });

    // Legacy descriptors remain resolvable for local artifact inspection/removal, never activation.
    public static AiLyricsModelDescriptor? Find(string id) => FindSelectable(id) ?? Legacy.FirstOrDefault(model => model.Id == id);
    public static AiLyricsModelDescriptor? FindSelectable(string id) => All.FirstOrDefault(model => model.Id == id);
    public static AiLyricsModelDescriptor? FindSelectableByHash(string? sha256) =>
        All.FirstOrDefault(model => string.Equals(model.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
}
