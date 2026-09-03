namespace DropSpace.Core.DragDrop;

public enum OleFileDataKind
{
    None,
    FileSystemPaths,
    ShellItems,
    VirtualFiles,
}

[Flags]
public enum OlePreferredDropEffect
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
}

public readonly record struct OleFileDataClassification(
    OleFileDataKind Kind,
    int ItemCount,
    OlePreferredDropEffect PreferredEffect,
    bool IsFileLikeEvidence,
    bool CanAcceptNow,
    bool CanMaterialize)
{
    public static OleFileDataClassification None { get; } = new(
        OleFileDataKind.None,
        0,
        OlePreferredDropEffect.None,
        false,
        false,
        false);

    public bool IsFileLike => IsFileLikeEvidence;

    // Visual authorization is the only condition that may reveal a Smart drag surface. Evidence
    // alone is deliberately insufficient: Shell data must resolve to real paths, while virtual
    // files are allowed because the real drop target can materialize them on Drop.
    public bool CanAuthorizeVisual => CanAcceptNow || CanMaterialize;

    // Compatibility alias for existing target code. New code should use the explicit capability.
    public bool CanAccept => CanAcceptNow;
}
