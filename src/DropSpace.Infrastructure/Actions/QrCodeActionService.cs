using DropSpace.Core.Actions;
using DropSpace.Infrastructure.Storage;
using QRCoder;

namespace DropSpace.Infrastructure.Actions;

public sealed class QrCodeActionService(AppStoragePaths paths) : IItemAction
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
            if (!string.IsNullOrWhiteSpace(path))
            {
                TryDelete(path);
            }

            throw;
        }

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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
