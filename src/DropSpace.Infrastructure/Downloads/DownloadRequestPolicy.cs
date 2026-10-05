using System.Net;
using System.Net.Http.Headers;

namespace DropSpace.Infrastructure.Downloads;

/// <summary>Caller-owned transport, headers and per-hop trust policy. Never enables auto redirects.</summary>
public sealed class DownloadRequestPolicy(HttpClient client, Func<Uri, bool> trusted, Action<HttpRequestMessage>? headers = null)
{
    public async Task<HttpResponseMessage> SendAsync(Uri uri, long? from, long? to, string? etag, CancellationToken token)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            if (!trusted(uri) || uri.UserInfo.Length != 0) throw new InvalidDataException("Untrusted download destination.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            headers?.Invoke(request);
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (from is not null) request.Headers.Range = new RangeHeaderValue(from, to);
            if (etag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Missing download redirect.");
                uri = new Uri(uri, location);
                continue;
            }
            return response;
        }
        throw new InvalidDataException("Too many download redirects.");
    }
}
