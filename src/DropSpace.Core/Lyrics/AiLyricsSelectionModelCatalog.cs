namespace DropSpace.Core.Lyrics;

// Separate activation role over the shared, hash-addressed model package store.
// These existing Hy identities preserve the earlier experimental selector only;
// they are not qualified Qwen selection profiles or promises of selection quality.
public static class AiLyricsSelectionModelCatalog
{
    public static AiLyricsModelDescriptor Default => AiLyricsModelCatalog.ExperimentalPlain;
    public static IReadOnlyList<AiLyricsModelDescriptor> All { get; } = Array.AsReadOnly(new[]
    {
        AiLyricsModelCatalog.ExperimentalPlain, AiLyricsModelCatalog.ExperimentalLargePlain,
    });

    // Qualification belongs to a complete model/runtime/protocol profile. A known
    // package hash or worker handshake alone does not establish recording accuracy.
    // None of the evaluated Hy/Qwen configurations has qualified for shipping.
    public static IReadOnlyList<AiLyricsModelDescriptor> Qualified { get; } = Array.Empty<AiLyricsModelDescriptor>();
    public static AiLyricsModelDescriptor? FindQualifiedByHash(string? sha256) =>
        Qualified.FirstOrDefault(model => string.Equals(model.Sha256, sha256, StringComparison.OrdinalIgnoreCase));

    public static AiLyricsModelDescriptor? FindSelectable(string? id) => All.FirstOrDefault(model => model.Id == id);
    public static AiLyricsModelDescriptor? FindSelectableByHash(string? sha256) =>
        All.FirstOrDefault(model => string.Equals(model.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
}
