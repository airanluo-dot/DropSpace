using DropSpace.Core.Actions;
using DropSpace.Infrastructure.Storage;
using QRCoder;
using System.Text;

namespace DropSpace.Infrastructure.Actions;

public sealed class QrCodeActionService(AppStoragePaths paths) : IItemAction
{
    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.GenerateQr, "ActionGenerateQr.Text", "QrCode", ItemActionGroup.Share, 20, true, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = selection.IsSingle &&
            CanEncode(selection.Single.Text ?? selection.Single.Url?.NormalizedUrl);
        return new ItemActionCapability(available, available ? null : "Select a text or URL item.", Descriptor);
    }

    public async Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default)
    {
        var capability = Evaluate(context.Selection);
        if (!capability.IsAvailable) return ItemActionResult.Failure("not-available", "ActionUnavailable");
        cancellationToken.ThrowIfCancellationRequested();
        var item = context.Selection.Single;
        var value = item.Text ?? item.Url?.NormalizedUrl
            ?? throw new InvalidDataException("The selected item has no QR payload.");
        var directory = ActionOutputPolicy.ResolveDirectory(paths, context.DestinationDirectory);
        Directory.CreateDirectory(directory);
        var baseName = string.IsNullOrWhiteSpace(item.Title) ? "DropSpace QR" : item.Title;
        var bytes = RenderPng(value);
        var path = string.Empty;
        try
        {
            await using var output = ActionOutputPolicy.CreateNewFile(
                directory,
                string.Concat(baseName, ".qr"),
                ".png",
                out path);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ActionOutputPolicy.TryDeleteIncompleteOutput(path);
            throw;
        }

        return ItemActionResult.Success([path], messageResourceKey: "ActionCompleted");
    }

    // Version 40, byte mode, ECC Q: reserve ECI/header overhead conservatively.
    public const int MaximumUtf8Bytes = 1600;

    public static bool CanEncode(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumUtf8Bytes && Encoding.UTF8.GetByteCount(value) <= MaximumUtf8Bytes;

    public static byte[] RenderPng(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!CanEncode(value)) throw new InvalidDataException("QR payload exceeds the bounded encoder capacity.");
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var renderer = new PngByteQRCode(data);
        return renderer.GetGraphic(8, drawQuietZones: true);
    }

}
