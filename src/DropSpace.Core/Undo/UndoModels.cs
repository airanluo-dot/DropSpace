namespace DropSpace.Core.Undo;

public enum UndoOperationKind
{
    RemoveItem = 1,
    RemoveBatch = 2,
    ClearClipboard = 3,
    PinChange = 4,
}

public sealed record UndoState(
    string Token,
    UndoOperationKind Kind,
    DateTimeOffset ExpiresAtUtc,
    string MessageResourceKey,
    int ItemCount);
