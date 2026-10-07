import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createLatestChangeApi } from "./release-contract.mjs";

const script = fileURLToPath(new URL("verify-published-release.mjs", import.meta.url));
const fixture = JSON.parse(await readFile(new URL("../data/releases.json", import.meta.url), "utf8"));
const release = fixture.api.releases.find(item => item.tagName === fixture.stable.tag);
const installerHash = "01".repeat(32);
const portableHash = "02".repeat(32);
const msixHash = "03".repeat(32);
const appCommit = "ab".repeat(20);
const repository = "airanluo-dot/DropSpace";
const runtimeId = "llama-cpp-v0.5.0-cuda13-win-x64-v1";
const componentTag = `cuda-${runtimeId}`;
const componentApiUrl = `https://api.github.com/repos/${repository}/releases/tags/${componentTag}`;
const componentBaseUrl = `https://github.com/${repository}/releases/download/${componentTag}/`;
const producer = JSON.parse(await readFile(new URL("../../../docs/dev/evidence/beta11-local-cuda/cuda13-producer-report.json", import.meta.url), "utf8"));
// Restore the original producer's CRLF manifest, without touching native files.
const cudaManifest = (await readFile(new URL("../../../docs/dev/evidence/beta11-local-cuda/cuda13-runtime-manifest.json", import.meta.url), "utf8"))
  .replace(/\r?\n/g, "\r\n");
const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");

async function verify(t, checksums, extraAssets = [], options = {}) {
  const appTag = options.appTag ?? release.tagName;
  const appUrl = `https://github.com/${repository}/releases/tag/${appTag}`;
  const prerelease = /-(?:preview|beta)\./.test(appTag);
  const directory = await mkdtemp(path.join(tmpdir(), "dropspace-published-verifier-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const bootstrap = path.join(directory, "fetch-fixture.mjs");
  const requestsFile = path.join(directory, "requests.json");
  const published = {
    draft: false, prerelease, html_url: appUrl, target_commitish: appCommit,
    assets: release.assets.map(asset => ({ name: asset.name, size: asset.size, browser_download_url: asset.downloadUrl.replace(release.tagName, appTag) })),
  };
  const api = structuredClone(fixture.api);
  const siteRelease = api.releases.find(item => item.tagName === release.tagName);
  Object.assign(siteRelease, { tagName: appTag, htmlUrl: appUrl, isPrerelease: prerelease });
  for (const asset of siteRelease.assets) asset.downloadUrl = asset.downloadUrl.replace(release.tagName, appTag);
  const bodies = {};
  for (const asset of extraAssets) {
    const downloadUrl = asset.url ?? `https://github.com/${repository}/releases/download/${appTag}/${asset.name}`;
    const size = asset.size ?? (asset.body === undefined ? 10 : Buffer.byteLength(asset.body));
    published.assets.push({ name: asset.name, size, digest: `sha256:${asset.hash}`, browser_download_url: downloadUrl });
    siteRelease.assets.push({ name: asset.name, size, kind: null, downloadUrl });
    if (asset.body !== undefined) bodies[downloadUrl] = asset.body;
  }
  Object.assign(published, options.published);
  const manifest = {
    version: appTag.slice(1), channel: prerelease ? "beta" : "stable", summary: "Published release",
    installer: { assetName: "DropSpaceSetup.exe", sha256: installerHash },
    portable: { assetName: "DropSpace.exe", sha256: portableHash },
  };
  const latest = createLatestChangeApi(api);
  const home = `<span data-latest-change-tag="">${latest.release.tagName}</span> Latest Stable · ${appTag} ${fixture.stable.assets.installer.replace(release.tagName, appTag)}`;
  await writeFile(bootstrap, `import { writeFileSync } from "node:fs";
const requests = [];
process.on("exit", () => writeFileSync(${JSON.stringify(requestsFile)}, JSON.stringify(requests)));
const responses = ${JSON.stringify({
    published, manifest, checksums, api, latest, home, bodies, component: options.component,
  })};
globalThis.fetch = async url => {
  const parsed = new URL(url);
  requests.push(parsed.href);
  if (parsed.href === ${JSON.stringify(`https://api.github.com/repos/${repository}/releases/tags/${appTag}`)}) return Response.json(responses.published);
  if (parsed.href === ${JSON.stringify(componentApiUrl)} && responses.component) return Response.json(responses.component);
  if (Object.hasOwn(responses.bodies, parsed.href)) return new Response(responses.bodies[parsed.href]);
  if (parsed.pathname.endsWith("update-manifest.json")) return Response.json(responses.manifest);
  if (parsed.pathname.endsWith("SHA256SUMS.txt")) return new Response(responses.checksums);
  if (parsed.pathname.endsWith("/api/v1/releases.json")) return Response.json(responses.api);
  if (parsed.pathname.endsWith("/api/v1/latest-change.json")) return Response.json(responses.latest);
  if (parsed.pathname.endsWith("/en/")) return new Response(responses.home);
  throw new Error("Unexpected request " + parsed.href);
};
`);
  return await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ["--import", pathToFileURL(bootstrap).href, script, appTag, "0", "live"], {
      env: { ...process.env, GITHUB_TOKEN: "", GH_TOKEN: "" }, stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", chunk => { stdout += chunk; });
    child.stderr.on("data", chunk => { stderr += chunk; });
    child.on("error", reject);
    child.on("close", async code => {
      try { resolve({ code, stdout, stderr, requests: JSON.parse(await readFile(requestsFile, "utf8")) }); }
      catch (error) { reject(error); }
    });
  });
}

const validChecksums = `${installerHash}  DropSpaceSetup.exe\n${portableHash}  DropSpace.exe\n${msixHash}  DropSpace-x64.msix\n`;

test("published verification accepts the exact three download checksums", async t => {
  const result = await verify(t, validChecksums);
  assert.equal(result.code, 0, result.stderr);
  assert.match(result.stdout, /Verified/);
});

test("published verification rejects a missing MSIX checksum with an unrelated third file", async t => {
  const result = await verify(t, validChecksums.replace("DropSpace-x64.msix", "unrelated.bin"));
  assert.notEqual(result.code, 0, "the exact public checksum contract must include MSIX");
  assert.match(result.stderr, /checksums|SHA256SUMS/);
});

test("published verification rejects duplicate checksum lines", async t => {
  const result = await verify(t, validChecksums + `${installerHash}  DropSpaceSetup.exe\n`);
  assert.notEqual(result.code, 0, "duplicate names must not disappear inside a Map");
  assert.match(result.stderr, /checksums|SHA256SUMS/);
});

const additionalRuntimeAssets = [
  { name: "runtime-publication.json", hash: "04".repeat(32) },
  { name: `DropSpace-CUDA-win-x64-${release.tagName}.zip`, hash: "05".repeat(32) },
];
const runtimeChecksums = additionalRuntimeAssets.map(asset => `${asset.hash}  ${asset.name}\n`).join("");

test("published verification includes same-release CUDA and runtime binding", async t => {
  const result = await verify(t, validChecksums + runtimeChecksums, additionalRuntimeAssets);
  assert.equal(result.code, 0, result.stderr);
  assert.match(result.stdout, /7 public assets/);
});

test("published verification rejects an uploaded CUDA digest mismatch", async t => {
  const result = await verify(t, validChecksums + runtimeChecksums.replace("05".repeat(32), "06".repeat(32)), additionalRuntimeAssets);
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /digest disagree/);
});

function cudaPublication(schemaVersion = 2) {
  const archiveName = schemaVersion === 1 ? `DropSpace-CUDA-win-x64-${release.tagName}.zip` : `DropSpace-CUDA-${runtimeId}.zip`;
  const descriptor = {
    schemaVersion, repository, appRelease: { tag: release.tagName, sourceCommit: appCommit },
    componentRelease: { tag: componentTag }, componentSourceCommit: producer.componentSourceCommit,
    runtimeId, backend: "cuda", platform: "win-x64", protocol: 1, profile: "hy-q8-plain-resident-v1",
    engineSourceCommit: producer.engineSourceCommit, workerSourceSha256: producer.workerSourceSha256,
    download: { ...producer.archive, name: archiveName,
      url: schemaVersion === 1 ? `https://github.com/${repository}/releases/download/${release.tagName}/${archiveName}` : `${componentBaseUrl}${archiveName}` },
    manifest: producer.manifest, files: producer.files, notices: producer.notices,
  };
  if (schemaVersion === 1) delete descriptor.componentRelease;
  return {
    descriptor, manifestBody: cudaManifest,
    // Public release 405423914 is a prerelease to avoid becoming the latest App.
    // Its publication source differs from the reviewed native producer source.
    component: { draft: false, prerelease: true, tag_name: componentTag, target_commitish: "6a1586a01e769c8b71492bcb18fb970047753434",
      html_url: `https://github.com/${repository}/releases/tag/${componentTag}`,
      assets: [descriptor.download, descriptor.manifest].map(identity => ({ name: identity.name,
        size: identity.bytes, digest: `sha256:${identity.sha256}`, browser_download_url: `${componentBaseUrl}${identity.name}` })) },
  };
}

async function verifyCuda(t, setup = () => {}, schemaVersion = 2) {
  const publication = cudaPublication(schemaVersion);
  setup(publication);
  const descriptorBody = JSON.stringify(publication.descriptor);
  const extraAssets = [
    additionalRuntimeAssets[0],
    { name: "cuda-runtime-download.json", body: descriptorBody, hash: sha256(descriptorBody) },
    { name: "cuda-runtime-manifest.json", body: publication.manifestBody, hash: sha256(publication.manifestBody) },
  ];
  if (schemaVersion === 1 || publication.duplicateZip) {
    extraAssets.push({ name: `DropSpace-CUDA-win-x64-${release.tagName}.zip`, size: producer.archive.bytes, hash: producer.archive.sha256 });
  }
  if (publication.adjustAssets) publication.adjustAssets(extraAssets);
  const checksums = validChecksums + extraAssets.map(asset => `${asset.hash}  ${asset.name}\n`).join("");
  return verify(t, checksums, extraAssets, publication);
}

test("published verification preserves schema-1 same-release CUDA metadata", async t => {
  const result = await verifyCuda(t, () => {}, 1);
  assert.equal(result.code, 0, result.stderr);
  assert.match(result.stdout, /9 public assets/);
  assert.ok(!result.requests.includes(componentApiUrl));
  assert.ok(result.requests.every(url => !url.endsWith(".zip")), "verification must never download CUDA binaries");
});

test("published verification accepts schema-2 metadata without a per-App CUDA ZIP", async t => {
  const result = await verifyCuda(t);
  assert.equal(result.code, 0, result.stderr);
  assert.match(result.stdout, /8 public assets/);
  assert.equal(result.requests.filter(url => url === componentApiUrl).length, 1);
  assert.ok(result.requests.every(url => !url.endsWith(".zip")), "component verification must use GitHub asset metadata only");
});

test("published verification reuses the independent component for a future Beta App", async t => {
  const result = await verifyCuda(t, p => {
    p.appTag = "v0.3.1-beta.31";
    p.descriptor.appRelease = { tag: p.appTag, sourceCommit: "cd".repeat(20) };
    p.published = { target_commitish: p.descriptor.appRelease.sourceCommit };
  });
  assert.equal(result.code, 0, result.stderr);
  assert.match(result.stdout, /Verified v0\.3\.1-beta\.31:.*8 public assets/);
  assert.ok(result.requests.includes(componentApiUrl));
  assert.ok(result.requests.every(url => !url.endsWith(".zip")));
});

test("published verification compares component file identities independently of JSON property order", async t => {
  const result = await verifyCuda(t, p => {
    p.descriptor.files = p.descriptor.files.map(({ name, bytes, sha256 }) => ({ bytes, sha256, name }));
  });
  assert.equal(result.code, 0, result.stderr);
});

for (const [name, mutate] of [
  ["unknown schema", p => { p.descriptor.schemaVersion = 3; }],
  ["App tag", p => { p.descriptor.appRelease.tag = "v9.9.9-beta.1"; }],
  ["App source", p => { p.descriptor.appRelease.sourceCommit = "cd".repeat(20); }],
  ["non-exact App source", p => { p.descriptor.appRelease.sourceCommit = "main"; p.published = { target_commitish: "main" }; }],
  ["repository", p => { p.descriptor.repository = "other/DropSpace"; }],
  ["component tag", p => { p.descriptor.componentRelease.tag = "cuda-unreviewed"; }],
  ["component source", p => { p.descriptor.componentSourceCommit = "cd".repeat(20); }],
  ["engine source", p => { p.descriptor.engineSourceCommit = "cd".repeat(20); }],
  ["worker source", p => { p.descriptor.workerSourceSha256 = "cd".repeat(32); }],
  ["runtime", p => { p.descriptor.runtimeId = "unreviewed"; }],
  ["backend", p => { p.descriptor.backend = "vulkan"; }],
  ["platform", p => { p.descriptor.platform = "win-arm64"; }],
  ["protocol", p => { p.descriptor.protocol = 2; }],
  ["string protocol", p => { p.descriptor.protocol = "1"; }],
  ["profile", p => { p.descriptor.profile = "unreviewed"; }],
  ["file identity", p => { p.descriptor.files = structuredClone(p.descriptor.files); p.descriptor.files[0].sha256 = "cd".repeat(32); }],
  ["archive name", p => { p.descriptor.download.name = "other.zip"; }],
  ["archive size", p => { p.descriptor.download.bytes++; }],
  ["archive digest", p => { p.descriptor.download.sha256 = "cd".repeat(32); }],
  ["manifest name", p => { p.descriptor.manifest = { ...p.descriptor.manifest, name: "other.json" }; }],
  ["manifest size", p => { p.descriptor.manifest = { ...p.descriptor.manifest, bytes: 1 }; }],
  ["manifest digest", p => { p.descriptor.manifest = { ...p.descriptor.manifest, sha256: "cd".repeat(32) }; }],
  ["duplicate per-App ZIP", p => { p.duplicateZip = true; }],
]) {
  test(`published verification rejects schema-2 ${name} mismatch before component lookup`, async t => {
    const result = await verifyCuda(t, mutate);
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /Published CUDA metadata/);
    assert.ok(!result.requests.includes(componentApiUrl));
  });
}

for (const [name, change] of [
  ["host", url => url.replace("github.com", "github.com.attacker.example")],
  ["repository", url => url.replace(repository, "other/DropSpace")],
  ["tag", url => url.replace(componentTag, "cuda-other")],
  ["protocol", url => url.replace("https:", "http:")],
  ["port", url => url.replace("github.com", "github.com:443")],
  ["query", url => `${url}?download=1`],
  ["fragment", url => `${url}#other`],
]) {
  test(`published verification rejects independent CUDA URL ${name} mismatch`, async t => {
    const result = await verifyCuda(t, p => { p.descriptor.download.url = change(p.descriptor.download.url); });
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /reviewed independent component/);
    assert.ok(!result.requests.includes(componentApiUrl));
  });
}

for (const [name, mutate] of [
  ["draft", c => { c.draft = true; }],
  ["prerelease", c => { c.prerelease = false; }],
  ["tag", c => { c.tag_name = "cuda-other"; }],
  ["release URL", c => { c.html_url += "?other"; }],
  ["missing archive", c => { c.assets.shift(); }],
  ["duplicate archive", c => { c.assets.push(c.assets[0]); }],
  ["archive name", c => { c.assets[0].name = "other.zip"; }],
  ["archive size", c => { c.assets[0].size++; }],
  ["archive digest", c => { c.assets[0].digest = `sha256:${"cd".repeat(32)}`; }],
  ["missing archive digest", c => { delete c.assets[0].digest; }],
  ["archive host", c => { c.assets[0].browser_download_url = c.assets[0].browser_download_url.replace("github.com", "attacker.example"); }],
  ["archive tag", c => { c.assets[0].browser_download_url = c.assets[0].browser_download_url.replace(componentTag, "cuda-other"); }],
  ["archive protocol", c => { c.assets[0].browser_download_url = c.assets[0].browser_download_url.replace("https:", "http:"); }],
  ["manifest size", c => { c.assets[1].size++; }],
  ["manifest digest", c => { c.assets[1].digest = `sha256:${"cd".repeat(32)}`; }],
  ["missing manifest", c => { c.assets.pop(); }],
]) {
  test(`published verification rejects published component ${name} mismatch`, async t => {
    const result = await verifyCuda(t, p => mutate(p.component));
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /[Ii]ndependent CUDA/);
    assert.ok(result.requests.includes(componentApiUrl));
    assert.ok(result.requests.every(url => !url.endsWith(".zip")));
  });
}

test("published verification rejects coordinated archive changes despite a source fingerprint", async t => {
  const result = await verifyCuda(t, p => {
    p.descriptor.download.sha256 = "cd".repeat(32);
    p.component.assets[0].digest = `sha256:${p.descriptor.download.sha256}`;
    p.descriptor.sourceFingerprint = "ef".repeat(32);
  });
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /reviewed independent component/);
});

test("published verification rejects changed manifest bytes even when all published hashes agree", async t => {
  const result = await verifyCuda(t, p => {
    const inner = JSON.parse(p.manifestBody);
    inner.protocol = 2;
    p.manifestBody = JSON.stringify(inner);
    p.descriptor.manifest = { ...p.descriptor.manifest, bytes: Buffer.byteLength(p.manifestBody), sha256: sha256(p.manifestBody) };
    Object.assign(p.component.assets[1], { size: p.descriptor.manifest.bytes, digest: `sha256:${p.descriptor.manifest.sha256}` });
  });
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /reviewed independent component/);
});

for (const missing of ["runtime-publication.json", "cuda-runtime-download.json", "cuda-runtime-manifest.json"]) {
  test(`published verification rejects CUDA metadata missing ${missing}`, async t => {
    const result = await verifyCuda(t, p => { p.adjustAssets = assets => assets.splice(assets.findIndex(asset => asset.name === missing), 1); });
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /exact public asset contract/);
  });
}

test("published verification keeps schema-1 dependent on its same-release ZIP", async t => {
  const result = await verifyCuda(t, p => { p.adjustAssets = assets => assets.pop(); }, 1);
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /Published CUDA metadata/);
});
