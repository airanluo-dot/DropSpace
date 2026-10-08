using System.Net;
using System.Text.Json;
using DropSpace.Core.Dlc;

namespace DropSpace.Infrastructure.Dlc;

/// <summary>Official metadata comes from the fixed publisher repository over HTTPS; hashes verify package integrity.</summary>
public sealed class OfficialModuleCatalog : IAsyncDisposable
{
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;
    public const int MaximumCatalogBytes = 256 * 1024;
    public static readonly Uri CatalogUri = new("https://raw.githubusercontent.com/airanluo-dot/DropSpace/main/modules/catalog.json");
    private static readonly Lazy<IReadOnlyList<OfficialModulePackage>> EmbeddedCatalog = new(ReadEmbedded);
    private readonly HttpClient _metadataClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeSync = new();
    private Task? _disposal;
    private IReadOnlyList<OfficialModulePackage> _packages = EmbeddedCatalog.Value;
    public IReadOnlyList<OfficialModulePackage> Packages => Volatile.Read(ref _packages);

    // A valid descriptor may be newer than this App. Protocol/capabilities are checked separately at activation.
    public static OfficialModulePackage Require(OfficialModulePackage package)
    {
        if (package is null || string.IsNullOrEmpty(package.Id) || !ModuleContract.ValidId(package.Id) ||
            string.IsNullOrEmpty(package.Version) || !ModuleContract.ValidVersion(package.Version) ||
            package.Bytes is <= 0 or > MaximumArchiveBytes || string.IsNullOrEmpty(package.Sha256) ||
            !ModulePackageStore.ValidHash(package.Sha256) || package.Url != PackageUrl(package.Id, package.Version) ||
            !Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host != "github.com" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("UnofficialModulePackage");
        return package;
    }

    public static string PackageUrl(string id, string version) =>
        $"https://github.com/airanluo-dot/DropSpace/releases/download/dlc-{id}-{version}/{id}-{version}-win-x64.zip";

    // New installs retain the official descriptor and original archive for offline integrity revalidation.
    public static OfficialModulePackage Find(string id, string version) =>
        EmbeddedCatalog.Value.FirstOrDefault(entry => entry.Id == id && entry.Version == version) ??
        throw new InvalidDataException("UnofficialModulePackage");

    /// <summary>Explicit metadata refresh only; no constructor/startup network or background polling.</summary>
    public async Task RefreshAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        token = deadline.Token;
        await _refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var response = await _metadataClient.GetAsync(CatalogUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != CatalogUri ||
                response.Content.Headers.ContentLength is > MaximumCatalogBytes)
                throw new InvalidDataException("OfficialModuleCatalogUnavailable");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = new byte[MaximumCatalogBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumCatalogBytes) throw new InvalidDataException("OfficialModuleCatalogTooLarge");
            var candidate = ReadCatalog(bytes.AsMemory(0, count));
            // The fixed HTTPS source establishes publisher provenance separately from archive hashes.
            // Preserve prior usable descriptors until the entire response is valid.
            Volatile.Write(ref _packages, candidate);
        }
        finally { _refresh.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _refresh.WaitAsync().ConfigureAwait(false);
        _metadataClient.Dispose();
        _refresh.Release(); _refresh.Dispose(); _lifetime.Dispose();
    }

    internal static bool TrustedHop(Uri destination, Uri original) =>
        destination.Scheme == Uri.UriSchemeHttps && destination.IsDefaultPort && destination.UserInfo.Length == 0 &&
        (destination == original || destination.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com");

    private static IReadOnlyList<OfficialModulePackage> ReadEmbedded()
    {
        using var input = typeof(OfficialModuleCatalog).Assembly.GetManifestResourceStream(
            "DropSpace.Infrastructure.Dlc.official-modules.json") ?? throw new InvalidDataException("OfficialModuleCatalogMissing");
        if (input.Length > MaximumCatalogBytes) throw new InvalidDataException("OfficialModuleCatalogTooLarge");
        using var output = new MemoryStream(); input.CopyTo(output);
        return ReadCatalog(output.ToArray());
    }

    private static IReadOnlyList<OfficialModulePackage> ReadCatalog(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        ExactProperties(document.RootElement, ["schemaVersion", "packages"]);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("OfficialModuleCatalogVersion");
        var records = document.RootElement.GetProperty("packages");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > 64) throw new InvalidDataException("OfficialModuleCatalogInvalid");
        var packages = new List<OfficialModulePackage>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.EnumerateArray())
        {
            ExactProperties(record, ["id", "version", "url", "bytes", "sha256"]);
            var package = record.Deserialize<OfficialModulePackage>(ModulePackageStore.JsonOptions) ?? throw new InvalidDataException("OfficialModuleCatalogInvalid");
            Require(package);
            if (!identities.Add(package.Id)) throw new InvalidDataException("OfficialModuleCatalogDuplicateId");
            packages.Add(package);
        }
        return packages.AsReadOnly();
    }

    private static void ExactProperties(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OfficialModuleCatalogInvalid");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("OfficialModuleCatalogInvalidField");
        if (seen.Count != expected.Length) throw new InvalidDataException("OfficialModuleCatalogMissingField");
    }

}
