using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Content;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class TextPreviewAdmissionTests
{
    [TestMethod]
    [DataRow(341, true)]
    [DataRow(342, false)]
    public async Task MultibyteInlineTextHonorsUtf8ByteLimit(int characters, bool allowed)
    {
        var provider = Provider();
        var text = new string('\u4e2d', characters);
        var request = Request(text);
        if (!allowed)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.LoadAsync(request));
            return;
        }

        var descriptor = await provider.LoadAsync(request);
        Assert.AreEqual(text, descriptor.Text);
        Assert.AreEqual(request.Item.Id, descriptor.ItemId);
        Assert.AreEqual(PreviewKind.Text, descriptor.Kind);
        Assert.AreEqual("1023", descriptor.Metadata["byteLength"]);
    }

    [TestMethod]
    public async Task PrecancelledInlinePreviewRejectsBeforeRendering()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            Provider().LoadAsync(Request("source text"), stop.Token));

        Assert.AreEqual(stop.Token, error.CancellationToken);
    }

    private static TextPreviewProvider Provider() => new(
        new ItemContentResolver(new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-inline-admission", Guid.NewGuid().ToString("N")))),
        new PreviewLimits { MaxTextBytes = 1024, InitialTextBytes = 1024 });

    private static PreviewRequest Request(string text) => new(new(Guid.NewGuid(), ItemKind.Text, ItemStatus.Available,
        "inline", null, null, null, "text/plain", text, null, 1));
}
