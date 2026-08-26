using DropSpace.Core.Actions;
using QRCoder;

namespace DropSpace.Infrastructure.Actions;

public sealed class QrCodeActionService : IItemAction
{
    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.GenerateQr, "ActionGenerateQr.Text", "QrCode", ItemActionGroup.Share, 20, true, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = selection.IsSingle &&
            (selection.Single.Text is { Length: > 0 } || selection.Single.Url?.NormalizedUrl is { Length: > 0 });
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
        var directory = context.DestinationDirectory ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);
        var baseName = string.IsNullOrWhiteSpace(item.Title) ? "DropSpace QR" : item.Title;
        var path = UniquePath(directory, string.Concat(baseName, ".qr.png"));
        var bytes = RenderPng(value);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return ItemActionResult.Success([path], messageResourceKey: "ActionCompleted");
    }

    public static byte[] RenderPng(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var renderer = new PngByteQRCode(data);
        return renderer.GetGraphic(8, drawQuietZones: true);
    }

    private static string UniquePath(string directory, string name)
    {
        var safe = string.Concat(name.Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        if (string.IsNullOrWhiteSpace(safe)) safe = "DropSpace QR.png";
        var path = Path.Combine(directory, safe);
        for (var index = 1; File.Exists(path); index++)
        {
            path = Path.Combine(directory, string.Concat(Path.GetFileNameWithoutExtension(safe), " (", index, ")", Path.GetExtension(safe)));
        }

        return path;
    }
}
