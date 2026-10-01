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

    public static IReadOnlyList<AiLyricsModelDescriptor> All { get; } = Array.AsReadOnly(new[] { Standard, Compact });

    public static AiLyricsModelDescriptor? Find(string id) => All.FirstOrDefault(model => model.Id == id);
}
