from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    # Preserve the repository's existing newline convention by writing bytes from
    # Python's normalized text only for files intentionally modified in this batch.
    (ROOT / path).write_bytes(text.encode("utf-8"))


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


def replace_all(path: str, old: str, new: str, expected: int) -> None:
    text = read(path)
    count = text.count(old)
    if count != expected:
        raise RuntimeError(f"{path}: expected {expected} matches, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new))


def insert_before(path: str, marker: str, block: str) -> None:
    replace_once(path, marker, block + marker)


# Remove pure line-ending noise that the first Linux repair runner surfaced.
noise_paths = [
    "scripts/Test-PortableSmoke.ps1",
    "src/DropSpace.App/OverlayWindow.xaml",
    "src/DropSpace.App/OverlayWindow.xaml.cs",
    "src/DropSpace.App/Services/OverlayWindowService.cs",
    "src/DropSpace.App/Services/WindowsCompatibilityService.cs",
    "src/DropSpace.Core/Compatibility/WindowsCompatibility.cs",
    "src/DropSpace.Core/Overlay/OverlayMotionController.cs",
    "src/DropSpace.Core/Overlay/OverlayStateMachine.cs",
    "tests/DropSpace.Infrastructure.Tests/StorageAndRepositoryTests.cs",
]
subprocess.run(["git", "checkout", "origin/main", "--", *noise_paths], cwd=ROOT, check=True)

# Finish AUD-011 for both startup-failure cleanup and normal Nearby Share disposal.
replace_all(
    "src/DropSpace.Infrastructure/Sharing/NearbyShareServer.cs",
    '''                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
''',
    '''                await app.StopAsync(CancellationToken.None).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
                await app.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
''',
    expected=2)

# Keep MainViewModel reconciliation on the UI synchronization context.
replace_once(
    "src/DropSpace.App/ViewModels/MainViewModel.cs",
    '''                var reconciliationFailures = await ReconcileSettingsStateAsync(previous).ConfigureAwait(false);
''',
    '''                var reconciliationFailures = await ReconcileSettingsStateAsync(previous);
''')
replace_once(
    "src/DropSpace.App/ViewModels/MainViewModel.cs",
    '''                    try { Settings = await _settingsService.LoadAsync(CancellationToken.None).ConfigureAwait(false); }
''',
    '''                    try { Settings = await _settingsService.LoadAsync(CancellationToken.None); }
''')
replace_once(
    "src/DropSpace.App/ViewModels/MainViewModel.cs",
    '''            try { Settings = await _settingsService.LoadAsync(CancellationToken.None).ConfigureAwait(false); }
''',
    '''            try { Settings = await _settingsService.LoadAsync(CancellationToken.None); }
''')
replace_once(
    "src/DropSpace.App/ViewModels/MainViewModel.cs",
    '''            try { await action().ConfigureAwait(false); }
''',
    '''            try { await action(); }
''')

# Preserve failed DropLink rollback paths in the failure snapshot so a residual
# file cannot become invisible if the filesystem refused rollback.
replace_once(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''    private static void RollbackCompletedItems(ReceiveTransfer receive)
    {
        while (receive.CompletedPaths.TryDequeue(out var relative))
        {
            try
            {
                var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(receive.DestinationRoot, relative);
                ReparseSafePathPolicy.RevalidatePreparedDestination(receive.DestinationRoot, destination);
                if (File.Exists(destination)) File.Delete(destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                System.Diagnostics.Debug.WriteLine($"DropLink rollback deferred: {exception.GetType().Name}");
            }
        }
    }
''',
    '''    private static void RollbackCompletedItems(ReceiveTransfer receive)
    {
        var residual = new List<string>();
        while (receive.CompletedPaths.TryDequeue(out var relative))
        {
            try
            {
                var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(receive.DestinationRoot, relative);
                ReparseSafePathPolicy.RevalidatePreparedDestination(receive.DestinationRoot, destination);
                if (File.Exists(destination)) File.Delete(destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                residual.Add(relative);
                System.Diagnostics.Debug.WriteLine($"DropLink rollback deferred: {exception.GetType().Name}");
            }
        }
        foreach (var relative in residual) receive.CompletedPaths.Enqueue(relative);
    }
''')

# AUD-008/AUD-009: bounded streaming JSON + fail-closed global/per-source share admission.
replace_once(
    "share-worker/src/index.js",
    '''async function createShare(request, env) {
  requireHttps(request);
  const body = await readJson(request, MAX_CREATE_REQUEST_BYTES);
''',
    '''async function createShare(request, env) {
  requireHttps(request);
  await requireCreateAdmission(request, env);
  const body = await readJson(request, MAX_CREATE_REQUEST_BYTES);
''')
insert_before(
    "share-worker/src/index.js",
    '''async function createShare(request, env) {
''',
    '''const CREATE_WINDOW_MS = 60_000;
const CREATE_SOURCE_LIMIT = 30;
const CREATE_GLOBAL_LIMIT = 600;

async function requireCreateAdmission(request, env) {
  const binding = env.SHARE_CREATION_LIMITER;
  if (!binding || typeof binding.idFromName !== "function" || typeof binding.get !== "function") {
    throw new HttpError("creation-limiter-unavailable", 503);
  }
  const address = request.headers.get("cf-connecting-ip") || "unknown";
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(address)));
  const sourceKey = [...digest.subarray(0, 16)].map(value => value.toString(16).padStart(2, "0")).join("");
  await admitCreation(binding, "global", CREATE_GLOBAL_LIMIT);
  await admitCreation(binding, "source-" + sourceKey, CREATE_SOURCE_LIMIT);
}

async function admitCreation(binding, key, limit) {
  const stub = binding.get(binding.idFromName(key));
  const response = await stub.fetch("https://dropspace-creation-limiter/admit", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ limit, windowMs: CREATE_WINDOW_MS }),
  });
  if (!response.ok) {
    let result = {};
    try { result = await response.json(); } catch { result = {}; }
    throw new HttpError(result.error || "creation-rate-limited", response.status === 429 ? 429 : 503);
  }
}

''')
replace_once(
    "share-worker/src/index.js",
    '''async function readJson(request, maximum) { const text = await request.text(); if (text.length > maximum) throw new HttpError("body-too-large", 413); try { return JSON.parse(text); } catch { throw new HttpError("json-invalid", 400); } }
''',
    '''async function readJson(request, maximum) {
  const declared = request.headers.get("content-length");
  if (declared !== null) {
    const length = Number(declared);
    if (!Number.isSafeInteger(length) || length < 0) throw new HttpError("body-length-invalid", 400);
    if (length > maximum) throw new HttpError("body-too-large", 413);
  }
  if (!request.body) throw new HttpError("body-missing", 400);
  const reader = request.body.getReader();
  const chunks = [];
  let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > maximum) {
        await reader.cancel("body-too-large").catch(() => {});
        throw new HttpError("body-too-large", 413);
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  let text;
  try { text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); }
  catch { throw new HttpError("json-invalid", 400); }
  try { return JSON.parse(text); } catch { throw new HttpError("json-invalid", 400); }
}
''')
insert_before(
    "share-worker/src/index.js",
    '''export class ShareUsageCoordinator {
''',
    '''export class ShareCreationLimiter {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    try {
      const body = await request.json();
      return await this.state.blockConcurrencyWhile(async () => {
        const limit = Number(body?.limit);
        const windowMs = Number(body?.windowMs);
        if (!Number.isInteger(limit) || limit < 1 || limit > 10_000 ||
            !Number.isInteger(windowMs) || windowMs < 1_000 || windowMs > 60 * 60 * 1000) {
          throw new HttpError("creation-limiter-request-invalid", 400);
        }
        const now = Date.now();
        let current = await this.state.storage.get("state");
        if (!current || !Number.isSafeInteger(current.windowStartedAt) ||
            now - current.windowStartedAt >= windowMs || now < current.windowStartedAt) {
          current = { windowStartedAt: now, count: 0 };
        }
        if (!Number.isInteger(current.count) || current.count < 0) current.count = 0;
        if (current.count >= limit) return coordinatorJson({ error: "creation-rate-limited" }, 429);
        current.count += 1;
        await this.state.storage.put("state", current);
        return coordinatorJson({ ok: true, remaining: Math.max(0, limit - current.count) });
      });
    } catch (error) {
      const status = error instanceof HttpError ? error.status : 500;
      return coordinatorJson({ error: error instanceof HttpError ? error.code : "creation-limiter-failed" }, status);
    }
  }
}

''')

replace_once(
    "share-worker/wrangler.toml.example",
    '''[[durable_objects.bindings]]
name = "SHARE_COORDINATOR"
class_name = "ShareUsageCoordinator"

[[migrations]]
tag = "v1"
new_classes = ["ShareUsageCoordinator"]
''',
    '''[[durable_objects.bindings]]
name = "SHARE_COORDINATOR"
class_name = "ShareUsageCoordinator"

[[durable_objects.bindings]]
name = "SHARE_CREATION_LIMITER"
class_name = "ShareCreationLimiter"

[[migrations]]
tag = "v1"
new_classes = ["ShareUsageCoordinator"]

[[migrations]]
tag = "v2"
new_classes = ["ShareCreationLimiter"]
''')
replace_once(
    "share-worker/README.md",
    '''The Worker requires the `SHARE_COORDINATOR` Durable Object binding shown in `wrangler.toml.example`. It is the authoritative concurrency-safe ledger for the token's aggregate item and plaintext-byte limits; requests fail closed when the binding is absent. The revoke path marks the coordinator first and paginates R2 deletion, so a large share cannot leave undeleted objects after the first listing page.
''',
    '''The Worker requires the `SHARE_COORDINATOR` Durable Object binding shown in `wrangler.toml.example`. It is the authoritative concurrency-safe ledger for the token's aggregate item and plaintext-byte limits; requests fail closed when the binding is absent. The revoke path marks the coordinator first and paginates R2 deletion, so a large share cannot leave undeleted objects after the first listing page. `SHARE_CREATION_LIMITER` is also mandatory: creation is admitted through a per-source hashed bucket and a global one-minute budget before the request body is read. Keep Cloudflare WAF/rate-limiting enabled as an additional edge layer rather than relying on application admission alone.
''')
replace_once(
    "share-worker/test/worker.test.mjs",
    '''import worker, { ShareUsageCoordinator } from "../src/index.js";
''',
    '''import worker, { ShareCreationLimiter, ShareUsageCoordinator } from "../src/index.js";
''')
insert_before(
    "share-worker/test/worker.test.mjs",
    '''test("the coordinator reserves concurrent plaintext byte usage atomically", async () => {
''',
    '''test("share creation limiter enforces a bounded window", async () => {
  const values = new Map();
  const limiter = new ShareCreationLimiter({
    storage: {
      async get(key) { return values.get(key); },
      async put(key, value) { values.set(key, value); },
    },
    async blockConcurrencyWhile(callback) { return callback(); },
  });
  const request = () => new Request("https://limiter/admit", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ limit: 2, windowMs: 60_000 }),
  });
  assert.equal((await limiter.fetch(request())).status, 200);
  assert.equal((await limiter.fetch(request())).status, 200);
  const limited = await limiter.fetch(request());
  assert.equal(limited.status, 429);
  assert.equal((await limited.json()).error, "creation-rate-limited");
});

''')

# AUD-002/AUD-017: main pushes validate only; explicit dispatch publishes and Stable must be signed.
replace_once(
    ".github/workflows/release.yml",
    '''      - name: Validate Stable and Preview release metadata
        shell: pwsh
        run: |
          ./scripts/Test-ReleaseVersion.ps1
          ./scripts/Test-ReleaseConsistency.ps1
''',
    '''      - name: Validate Stable and Preview release metadata
        shell: pwsh
        run: |
          ./scripts/Test-ReleaseVersion.ps1
          ./scripts/Test-ReleaseConsistency.ps1

      - name: Require signing credentials for explicit Stable publication
        if: github.event_name == 'workflow_dispatch' && steps.version.outputs.channel == 'Stable'
        shell: pwsh
        run: |
          if ($env:SIGNING_ENABLED -ne 'true') {
            throw "Stable publication requires configured Artifact Signing credentials."
          }
''')
replace_once(
    ".github/workflows/release.yml",
    '''    if: github.event_name != 'pull_request'
''',
    '''    if: github.event_name == 'workflow_dispatch'
''')

# Regression coverage for retention result/pinned invariants and deferred payload cleanup.
write(
    "tests/DropSpace.Infrastructure.Tests/AuditHardeningTests.cs",
    '''using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AuditHardeningTests
{
    [TestMethod]
    public async Task RetentionNeverDeletesPinnedClipboardItemsAndReportsActualRows()
    {
        var paths = Paths();
        try
        {
            var repository = Repository(paths);
            var first = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("first"));
            var second = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("second"));
            await repository.SetPinnedAsync(first.Id, true);

            var result = await repository.ApplyRetentionAsync(DateTimeOffset.UtcNow.AddDays(1), 1);
            Assert.AreEqual(1, result.RemovedCount);
            Assert.IsNotNull(await repository.GetAsync(first.Id));
            Assert.IsNull(await repository.GetAsync(second.Id));
        }
        finally { Cleanup(paths); }
    }

    [TestMethod]
    public async Task PayloadDeleteFailureIsRetriedByTheNextStoreInstance()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows sharing semantics are required.");
        var paths = Paths();
        try
        {
            var store = new FilePayloadStore(paths);
            await using var source = new MemoryStream([1, 2, 3, 4]);
            var payload = await store.WriteAsync("files", source, 1024);
            var fullPath = store.ResolvePath(payload.RelativePath);
            await using (var locked = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(payload.RelativePath));
            }

            Assert.IsTrue(File.Exists(fullPath));
            _ = new FilePayloadStore(paths);
            Assert.IsFalse(File.Exists(fullPath));
        }
        finally { Cleanup(paths); }
    }

    private static AppStoragePaths Paths() =>
        new(Path.Combine(Path.GetTempPath(), "DropSpace-audit-tests", Guid.NewGuid().ToString("N")));

    private static SqliteItemRepository Repository(AppStoragePaths paths)
    {
        var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
        return new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
    }

    private static void Cleanup(AppStoragePaths paths)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true);
    }
}
''')

print("Applied architecture audit fixes batch 2.")
