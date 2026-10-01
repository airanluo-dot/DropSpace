namespace DropSpace.Core.Lyrics;

public sealed record AiLyricsModelDescriptor(string Id, string Name, Uri DownloadUri, long Bytes, string Sha256);

public static class AiLyricsModelCatalog
{
    public static AiLyricsModelDescriptor Standard { get; } = new(
        "hy-mt2-standard", "Hy-MT2 1.8B Q4_K_M",
        new Uri("https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q4_K_M.gguf"),
        1_133_080_448,
        "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699");

    public static AiLyricsModelDescriptor? Find(string id) => id == Standard.Id ? Standard : null;
}
