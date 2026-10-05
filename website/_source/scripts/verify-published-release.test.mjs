import assert from "node:assert/strict";
import { spawn } from "node:child_process";
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

async function verify(t, checksums, extraAssets = []) {
  const directory = await mkdtemp(path.join(tmpdir(), "dropspace-published-verifier-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const bootstrap = path.join(directory, "fetch-fixture.mjs");
  const published = {
    draft: false, prerelease: false, html_url: release.htmlUrl,
    assets: release.assets.map(asset => ({ name: asset.name, size: asset.size, browser_download_url: asset.downloadUrl })),
  };
  const api = structuredClone(fixture.api);
  for (const asset of extraAssets) {
    const downloadUrl = `https://github.com/airanluo-dot/DropSpace/releases/download/${release.tagName}/${asset.name}`;
    published.assets.push({ name: asset.name, size: 10, digest: `sha256:${asset.hash}`, browser_download_url: downloadUrl });
    api.releases.find(item => item.tagName === release.tagName).assets.push({ name: asset.name, size: 10, kind: null, downloadUrl });
  }
  const manifest = {
    version: release.tagName.slice(1), channel: "stable", summary: "Published Stable release",
    installer: { assetName: "DropSpaceSetup.exe", sha256: installerHash },
    portable: { assetName: "DropSpace.exe", sha256: portableHash },
  };
  const latest = createLatestChangeApi(fixture.api);
  const home = `<span data-latest-change-tag="">${latest.release.tagName}</span> Latest Stable · ${release.tagName} ${fixture.stable.assets.installer}`;
  await writeFile(bootstrap, `const responses = ${JSON.stringify({
    published, manifest, checksums, api, latest, home,
  })};
globalThis.fetch = async url => {
  const parsed = new URL(url);
  if (parsed.hostname === "api.github.com") return Response.json(responses.published);
  if (parsed.pathname.endsWith("update-manifest.json")) return Response.json(responses.manifest);
  if (parsed.pathname.endsWith("SHA256SUMS.txt")) return new Response(responses.checksums);
  if (parsed.pathname.endsWith("/api/v1/releases.json")) return Response.json(responses.api);
  if (parsed.pathname.endsWith("/api/v1/latest-change.json")) return Response.json(responses.latest);
  if (parsed.pathname.endsWith("/en/")) return new Response(responses.home);
  throw new Error("Unexpected request " + parsed.href);
};
`);
  return await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ["--import", pathToFileURL(bootstrap).href, script, release.tagName, "0", "live"], {
      env: { ...process.env, GITHUB_TOKEN: "", GH_TOKEN: "" }, stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", chunk => { stdout += chunk; });
    child.stderr.on("data", chunk => { stderr += chunk; });
    child.on("error", reject);
    child.on("close", code => resolve({ code, stdout, stderr }));
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
