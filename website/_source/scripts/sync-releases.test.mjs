import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

const websiteRoot = fileURLToPath(new URL("..", import.meta.url));
const script = fileURLToPath(new URL("sync-releases.mjs", import.meta.url));
const committedFixture = fileURLToPath(new URL("../data/releases.json", import.meta.url));

function githubRelease(tag, { prerelease, publishedAt }) {
  const names = ["DropSpaceSetup.exe", "DropSpace.exe", "DropSpace-x64.msix", "SHA256SUMS.txt", "update-manifest.json"];
  return {
    tag_name: tag,
    name: `DropSpace ${tag}`,
    body: `# DropSpace ${tag}\n\n- Tested release`,
    draft: false,
    prerelease,
    published_at: publishedAt,
    html_url: `https://github.com/airanluo-dot/DropSpace/releases/tag/${tag}`,
    assets: names.map((name, index) => ({
      name,
      size: 100 + index,
      browser_download_url: `https://github.com/airanluo-dot/DropSpace/releases/download/${tag}/${name}`
    }))
  };
}

const currentPayload = [
  githubRelease("v0.2.0-preview.5", { prerelease: true, publishedAt: "2026-08-14T02:00:00Z" }),
  githubRelease("v0.1.0", { prerelease: false, publishedAt: "2026-08-11T02:00:00Z" })
];

async function runCli(args = [], env = {}) {
  return await new Promise((resolve) => {
    const child = spawn(process.execPath, [script, ...args], {
      cwd: websiteRoot,
      env: { ...process.env, ...env },
      stdio: ["ignore", "pipe", "pipe"]
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("close", (code) => resolve({ code, stdout, stderr }));
  });
}

async function withServer(handler, callback) {
  const server = createServer(handler);
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  try {
    return await callback(`http://127.0.0.1:${port}/releases`);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

async function runAgainst(apiUrl, output) {
  return runCli([], {
    NODE_ENV: "test",
    DROPSPACE_TEST_RELEASES_API: apiUrl,
    DROPSPACE_TEST_RELEASES_OUTPUT: output
  });
}

test("authoritative sync writes the latest Stable and Preview contract", async (t) => {
  const directory = await mkdtemp(path.join(tmpdir(), "dropspace-release-sync-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const output = path.join(directory, "releases.json");
  const result = await withServer((request, response) => {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(JSON.stringify(currentPayload));
  }, (url) => runAgainst(url, output));

  assert.equal(result.code, 0, result.stderr);
  const data = JSON.parse(await readFile(output, "utf8"));
  assert.equal(data.stable.tag, "v0.1.0");
  assert.equal(data.previews[0].tag, "v0.2.0-preview.5");
  assert.equal(data.api.schemaVersion, 1);
});

test("network failure exits non-zero and never replaces an existing stale fixture", async (t) => {
  const directory = await mkdtemp(path.join(tmpdir(), "dropspace-release-sync-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const output = path.join(directory, "releases.json");
  const stale = await readFile(committedFixture, "utf8");
  await writeFile(output, stale);

  const server = createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  await new Promise((resolve) => server.close(resolve));
  const result = await runAgainst(`http://127.0.0.1:${port}/releases`, output);

  assert.notEqual(result.code, 0);
  assert.equal(await readFile(output, "utf8"), stale);
  assert.doesNotMatch(`${result.stdout}\n${result.stderr}`, /using committed fallback/i);
});

test("HTTP 500 exits non-zero", async (t) => {
  const directory = await mkdtemp(path.join(tmpdir(), "dropspace-release-sync-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const result = await withServer((request, response) => {
    response.writeHead(500);
    response.end("upstream failure");
  }, (url) => runAgainst(url, path.join(directory, "releases.json")));
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /HTTP 500/);
});

test("invalid JSON exits non-zero", async (t) => {
  const directory = await mkdtemp(path.join(tmpdir(), "dropspace-release-sync-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const result = await withServer((request, response) => {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end("{not-json");
  }, (url) => runAgainst(url, path.join(directory, "releases.json")));
  assert.notEqual(result.code, 0);
});

test("unofficial release and asset URLs exit non-zero", async (t) => {
  const directory = await mkdtemp(path.join(tmpdir(), "dropspace-release-sync-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const cases = [
    currentPayload.map((release, index) => index === 0 ? { ...release, html_url: "https://attacker.invalid/release" } : release),
    currentPayload.map((release, index) => index === 0 ? {
      ...release,
      assets: release.assets.map((asset, assetIndex) => assetIndex === 0 ? {
        ...asset,
        browser_download_url: "https://attacker.invalid/DropSpaceSetup.exe"
      } : asset)
    } : release)
  ];

  for (const [index, payload] of cases.entries()) {
    const result = await withServer((request, response) => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(payload));
    }, (url) => runAgainst(url, path.join(directory, `releases-${index}.json`)));
    assert.notEqual(result.code, 0);
  }
});

test("explicit fixture mode is local-only and production rejects it", async () => {
  const local = await runCli(["--fixture", committedFixture, "--validate-only"], { NODE_ENV: "test" });
  assert.equal(local.code, 0, local.stderr);

  const production = await runCli(["--fixture", committedFixture, "--validate-only"], { NODE_ENV: "production" });
  assert.notEqual(production.code, 0);
  assert.match(production.stderr, /forbidden in production/i);
});

test("production workflow cannot bypass authoritative sync failures", async () => {
  const workflow = await readFile(new URL("../../../.github/workflows/deploy-website.yml", import.meta.url), "utf8");
  const syncStep = workflow.indexOf("npm run sync-releases");
  const buildStep = workflow.indexOf("npm test");
  assert.ok(syncStep >= 0 && buildStep > syncStep);
  assert.match(workflow, /NODE_ENV: production/);
  assert.doesNotMatch(workflow, /continue-on-error\s*:\s*true|npm run sync-releases\s*\|\|\s*true|--fixture/);
});
