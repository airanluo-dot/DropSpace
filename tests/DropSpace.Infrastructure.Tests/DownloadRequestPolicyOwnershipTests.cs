using System.Net;
using DropSpace.Infrastructure.Downloads;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DownloadRequestPolicyOwnershipTests
{
    [TestMethod]
    [DataRow(HttpStatusCode.OK)]
    [DataRow(HttpStatusCode.Found)]
    public async Task ObserverFailureDisposesResponseBeforeItCanBeHandedToCaller(HttpStatusCode status)
    {
        var content = new TrackingContent();
        using var response = new HttpResponseMessage(status) { Content = content };
        using var client = new HttpClient(new Handler(_ => response));
        var failure = new InvalidOperationException("Fixture observer failed after response headers.");
        var policy = new DownloadRequestPolicy(client, _ => true, observer: (_, observedStatus) =>
        {
            if (observedStatus is not null) throw failure;
        });

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            policy.SendAsync(new Uri("https://example.com/file"), null, null, null, CancellationToken.None));

        Assert.AreSame(failure, thrown);
        Assert.IsTrue(content.IsDisposed, "A response hidden by an observer exception still needs its body released.");
    }

    [TestMethod]
    public async Task RedirectBodyIsDisposedButSuccessfulResponseBelongsToCaller()
    {
        var redirectContent = new TrackingContent();
        var finalContent = new TrackingContent();
        using var redirect = new HttpResponseMessage(HttpStatusCode.Found) { Content = redirectContent };
        redirect.Headers.Location = new Uri("/final", UriKind.Relative);
        using var final = new HttpResponseMessage(HttpStatusCode.OK) { Content = finalContent };
        var calls = new List<Uri>();
        using var client = new HttpClient(new Handler(request =>
        {
            calls.Add(request.RequestUri!);
            return calls.Count == 1 ? redirect : final;
        }));
        var policy = new DownloadRequestPolicy(client, uri => uri.Host == "example.com");

        var result = await policy.SendAsync(new Uri("https://example.com/start"), null, null, null, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { new Uri("https://example.com/start"), new Uri("https://example.com/final") }, calls);
        Assert.IsTrue(redirectContent.IsDisposed);
        Assert.AreSame(final, result);
        Assert.IsFalse(finalContent.IsDisposed, "The caller must be able to consume the successful response.");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await result.Content.ReadAsByteArrayAsync());
        result.Dispose();
        Assert.IsTrue(finalContent.IsDisposed);
    }

    [TestMethod]
    public async Task RejectedRedirectReleasesPreviousBodyWithoutContactingUntrustedDestination()
    {
        var content = new TrackingContent();
        using var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = content };
        response.Headers.Location = new Uri("https://untrusted.invalid/file");
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return response;
        }));
        var policy = new DownloadRequestPolicy(client, uri => uri.Host == "example.com");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            policy.SendAsync(new Uri("https://example.com/file"), null, null, null, CancellationToken.None));

        Assert.AreEqual(1, calls);
        Assert.IsTrue(content.IsDisposed);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool IsDisposed { get; private set; }

        protected override bool TryComputeLength(out long length) { length = 3; return true; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
