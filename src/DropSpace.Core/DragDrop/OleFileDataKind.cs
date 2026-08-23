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
    bool CanAccept)
{
    public static OleFileDataClassification None { get; } = new(
        OleFileDataKind.None,
        0,
        OlePreferredDropEffect.None,
        false);

    public bool IsFileLike => Kind != OleFileDataKind.None;
}
