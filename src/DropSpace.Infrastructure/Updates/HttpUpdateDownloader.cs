using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Updates;

public sealed class HttpUpdateDownloader(
    HttpClient client,
    AppStoragePaths paths,
    UpdateStateStore stateStore) : IUpdateDownloader
{
    public async Task<DownloadedUpdate> DownloadAsync(
        UpdateCandidate candidate,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var descriptor = candidate.DeploymentMode switch
        {
            DeploymentMode.Installer => candidate.Manifest.Installer,
            DeploymentMode.Portable => candidate.Manifest.Portable,
            _ => throw new InvalidOperationException("Packaged deployments are updated by Windows and cannot download an Inno payload."),
        };
        if (!string.Equals(descriptor.AssetName, candidate.SelectedAsset.Name, StringComparison.Ordinal) ||
            descriptor.Size != candidate.SelectedAsset.Size ||
            !UpdateManifestParser.IsOfficialDownloadUri(
                candidate.SelectedAsset.DownloadUri,
                candidate.Release.TagName,
                candidate.SelectedAsset.Name))
        {
            throw new InvalidDataException("The selected download does not match the validated update manifest.");
        }

        paths.EnsureCreated();
        var versionDirectory = GetContainedVersionDirectory(candidate.Manifest.Version);
        Directory.CreateDirectory(versionDirectory);
        var finalPath = GetContainedChildPath(versionDirectory, descriptor.AssetName);
        var partialPath = string.Concat(finalPath, ".download");
        var logPath = GetContainedChildPath(versionDirectory, "update-install.log");
        if (File.Exists(finalPath))
        {
            var existing = new DownloadedUpdate(candidate, finalPath, descriptor.Size, descriptor.Sha256, logPath);
            if (await VerifyFileAsync(existing, cancellationToken).ConfigureAwait(false))
            {
                await stateStore.SaveAsync(existing, "ReadyToInstall", cancellationToken).ConfigureAwait(false);
                return existing;
            }

            File.Delete(finalPath);
        }

        var resumeOffset = GetResumeOffset(partialPath, descriptor.Size);
        HttpResponseMessage? response = null;
        try
        {
            response = await SendDownloadRequestAsync(candidate, resumeOffset, cancellationToken).ConfigureAwait(false);
            var append = resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                response = null;
                TryDeletePartial(partialPath);
                resumeOffset = 0;
                response = await SendDownloadRequestAsync(candidate, 0, cancellationToken).ConfigureAwait(false);
                append = false;
            }

            response.EnsureSuccessStatusCode();
            if (append)
            {
                ValidateResumeResponse(response, resumeOffset, descriptor.Size);
            }
            else
            {
                if (resumeOffset > 0)
                {
                    // The server ignored Range and returned the complete object. Restart the
                    // local staging file rather than appending duplicate bytes.
                    TryDeletePartial(partialPath);
                    resumeOffset = 0;
                }
                if (response.Content.Headers.ContentLength is long contentLength && contentLength != descriptor.Size)
                {
                    throw new InvalidDataException("The update Content-Length does not match the signed-off manifest size.");
                }
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (append)
            {
                await HashExistingPartialAsync(partialPath, hash, cancellationToken).ConfigureAwait(false);
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                append ? FileMode.Append : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            var total = resumeOffset;
            if (total > 0) progress?.Report(new UpdateDownloadProgress(total, descriptor.Size));
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > descriptor.Size)
                {
                    throw new InvalidDataException("The update stream exceeded the manifest size.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new UpdateDownloadProgress(total, descriptor.Size));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != descriptor.Size)
            {
                throw new EndOfStreamException("The update stream ended before the manifest size was reached.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(descriptor.Sha256)))
            {
                throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            }

            output.Close();
            File.Move(partialPath, finalPath, true);
            var downloaded = new DownloadedUpdate(candidate, finalPath, total, actualHash, logPath);
            await stateStore.SaveAsync(downloaded, "ReadyToInstall", cancellationToken).ConfigureAwait(false);
            return downloaded;
        }
        catch (InvalidDataException)
        {
            TryDeletePartial(partialPath);
            throw;
        }
        catch (EndOfStreamException)
        {
            // A cleanly truncated or disconnected response is resumable. The next request
            // hashes the staged prefix again before appending and the final manifest hash
            // remains authoritative.
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (IOException)
        {
            // Preserve a bounded, version-scoped prefix for a later retry. Any corrupt or
            // externally modified prefix can only reach ReadyToInstall after the full SHA-256
            // matches the signed-off manifest.
            throw;
        }
        catch
        {
            TryDeletePartial(partialPath);
            throw;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        UpdateCandidate candidate,
        long offset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, candidate.SelectedAsset.DownloadUri);
        request.Headers.UserAgent.ParseAdd($"DropSpace/{candidate.Manifest.Version}");
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }
        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static long GetResumeOffset(string partialPath, long expectedSize)
    {
        if (!File.Exists(partialPath)) return 0;
        var length = new FileInfo(partialPath).Length;
        if (length <= 0 || length >= expectedSize)
        {
            TryDeletePartial(partialPath);
            return 0;
        }
        return length;
    }

    private static void ValidateResumeResponse(HttpResponseMessage response, long offset, long expectedSize)
    {
        var range = response.Content.Headers.ContentRange;
        if (range is null ||
            !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            range.From != offset ||
            range.To != expectedSize - 1 ||
            range.Length != expectedSize)
        {
            throw new InvalidDataException("The update server returned an invalid Content-Range for resume.");
        }
        var expectedRemaining = expectedSize - offset;
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != expectedRemaining)
        {
            throw new InvalidDataException("The resumed update Content-Length does not match the manifest remainder.");
        }
    }

    private static async Task HashExistingPartialAsync(
        string partialPath,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            partialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Update partial cleanup deferred: {exception.GetType().Name}");
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Update partial cleanup deferred: {exception.GetType().Name}");
        }
    }

    internal async Task<bool> VerifyFileAsync(DownloadedUpdate update, CancellationToken cancellationToken)
    {
        if (!File.Exists(update.FilePath)) return false;
        var info = new FileInfo(update.FilePath);
        if (info.Length != update.Size) return false;
        await using var stream = new FileStream(
            update.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(update.Sha256));
    }

    private string GetContainedVersionDirectory(ReleaseVersion version) =>
        GetContainedChildPath(paths.Updates, version.ToString());

    private static string GetContainedChildPath(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException("Update cache names may not contain path separators.");
        }

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, name));
        var relative = Path.GetRelativePath(fullRoot, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("The update cache path escaped the DropSpace-owned root.");
        }

        return candidate;
    }
}