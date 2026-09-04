using DropSpace.Core.Models;
using DropSpace.Core.Preview;

namespace DropSpace.Core.Actions;

public enum ItemActionGroup
{
    General = 0,
    Preview = 1,
    Transform = 2,
    Transfer = 3,
    Share = 4,
}

public enum ItemActionId
{
    Open = 1,
    ShowInFolder = 2,
    Copy = 3,
    CopyPath = 4,
    Preview = 5,
    HashSha256 = 6,
    CompressZip = 7,
    ResizeImage = 8,
    ConvertImage = 9,
    StripMetadata = 10,
    GenerateQr = 11,
    SendToDevice = 12,
    CreateNearbyLink = 13,
    CreateSecureInternetLink = 14,
}

public sealed record ItemActionDescriptor(
    ItemActionId Id,
    string LabelResourceKey,
    string Icon,
    ItemActionGroup Group,
    int Order,
    bool RequiresSingleItem,
    bool IsDestructive);

public sealed record ItemActionCapability(
    bool IsAvailable,
    string? Reason,
    ItemActionDescriptor Descriptor);

public sealed record ItemSelectionSnapshot(IReadOnlyList<DropItemSnapshot> Items)
{
    public bool IsEmpty => Items.Count == 0;
    public bool IsSingle => Items.Count == 1;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Single is the established public selector in the item selection contract.")]
    public DropItemSnapshot Single => IsSingle
        ? Items[0]
        : throw new InvalidOperationException("The selection does not contain exactly one item.");
}

public sealed record ItemActionContext(
    ItemSelectionSnapshot Selection,
    string? DestinationDirectory = null,
    string? OutputFormat = null,
    int? Width = null,
    int? Height = null,
    bool KeepAspectRatio = true,
    CancellationToken CancellationToken = default);

public sealed record ItemActionResult(
    bool Succeeded,
    string? MessageResourceKey,
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<Guid> OutputItemIds,
    string? ErrorCategory = null)
{
    public static ItemActionResult Success(
        IReadOnlyList<string>? outputPaths = null,
        IReadOnlyList<Guid>? outputItemIds = null,
        string? messageResourceKey = null) =>
        new(true, messageResourceKey, outputPaths ?? [], outputItemIds ?? []);

    public static ItemActionResult Failure(string errorCategory, string? messageResourceKey = null) =>
        new(false, messageResourceKey, [], [], errorCategory);
}

public interface IItemAction
{
    ItemActionDescriptor Descriptor { get; }

    ItemActionCapability Evaluate(ItemSelectionSnapshot selection);

    Task<ItemActionResult> ExecuteAsync(
        ItemActionContext context,
        CancellationToken cancellationToken = default);
}

public interface IItemActionRegistry
{
    IReadOnlyList<IItemAction> Actions { get; }

    IReadOnlyList<ItemActionCapability> Evaluate(ItemSelectionSnapshot selection);

    IReadOnlyList<ItemActionCapability> EvaluatePrimary(ItemSelectionSnapshot selection);

    IReadOnlyList<ItemActionCapability> EvaluateMore(ItemSelectionSnapshot selection);

    Task<ItemActionResult> ExecuteAsync(
        ItemActionId actionId,
        ItemActionContext context,
        CancellationToken cancellationToken = default);
}

public interface IImageTransformService
{
    Task<ItemActionResult> ResizeAsync(
        DropItemSnapshot item,
        string destinationDirectory,
        int width,
        int height,
        bool keepAspectRatio,
        string? outputFormat,
        bool stripMetadata,
        CancellationToken cancellationToken = default);

    Task<ItemActionResult> ConvertAsync(
        DropItemSnapshot item,
        string destinationDirectory,
        string outputFormat,
        int? width = null,
        int? height = null,
        bool keepAspectRatio = true,
        CancellationToken cancellationToken = default);

    Task<ItemActionResult> StripMetadataAsync(
        DropItemSnapshot item,
        string destinationDirectory,
        string? outputFormat = null,
        CancellationToken cancellationToken = default);
}
