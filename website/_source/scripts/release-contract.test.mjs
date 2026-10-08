import assert from "node:assert/strict";
import test from "node:test";
import { createLatestChangeApi, createWebsiteReleaseData, normalizeGitHubReleases, validateLatestChangeApi, validateReleaseApi } from "./release-contract.mjs";

const valid = {
  tag_name: "v0.2.0-preview.2",
  name: "DropSpace v0.2.0-preview.2",
  body: "- Fix",
  draft: false,
  prerelease: true,
  published_at: "2026-08-12T00:00:00Z",
  html_url: "https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.0-preview.2",
  assets: [{
    name: "DropSpaceSetup.exe",
    size: 42,
    browser_download_url: "https://github.com/airanluo-dot/DropSpace/releases/download/v0.2.0-preview.2/DropSpaceSetup.exe"
  }]
};

test("normalizes the public GitHub contract without arbitrary URLs", () => {
  const api = normalizeGitHubReleases([valid, { ...valid, draft: true }], "2026-08-12T00:00:00Z");
  assert.equal(api.schemaVersion, 1);
  assert.equal(api.releases.length, 1);
  assert.equal(api.releases[0].assets[0].size, 42);
  assert.equal(validateReleaseApi(api), api);
});

test("model and pinned CUDA resources never enter App feeds or release selection", () => {
  const stable = {
    ...valid,
    tag_name: "v0.1.0",
    prerelease: false,
    html_url: "https://github.com/airanluo-dot/DropSpace/releases/tag/v0.1.0",
    assets: ["DropSpaceSetup.exe", "DropSpace.exe", "DropSpace-x64.msix", "SHA256SUMS.txt", "update-manifest.json"].map((name) => ({
      name,
      size: 42,
      browser_download_url: `https://github.com/airanluo-dot/DropSpace/releases/download/v0.1.0/${name}`
    }))
  };
  const resources = ["models-hy-mt2-q8-v1", "cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1"].map((tag_name) => ({
    tag_name,
    draft: false,
    prerelease: false,
    published_at: "2026-10-07T00:00:00Z",
    assets: []
  }));
  const generatedAt = "2026-10-07T01:00:00Z";
  const payload = [...Array(21).fill(resources[1]), resources[0], stable];
  const api = normalizeGitHubReleases(payload, generatedAt);
  assert.deepEqual(api.releases.map((release) => release.tagName), [stable.tag_name]);
  assert.equal(createLatestChangeApi(api).release.tagName, stable.tag_name);
  assert.deepEqual(createWebsiteReleaseData(payload, generatedAt), createWebsiteReleaseData([stable], generatedAt));
  assert.throws(() => createWebsiteReleaseData(resources, generatedAt), /did not contain a Stable release/);
});

test("resource filtering still rejects unrecognized or malformed release tags", () => {
  for (const tag_name of [
    "not-a-release",
    "v0.3.1-beta.invalid",
    "models-hy-mt2-q8-v1-extra",
    "cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1-extra",
    "prefix-cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1",
    "cuda-llama-cpp-v0.5.0-cuda13-win-arm64-v1",
    "cuda-llama-cpp-v0.5.0-cuda13-win-x64-v2"
  ]) {
    const release = { ...valid, tag_name };
    assert.throws(() => normalizeGitHubReleases([release]), /Invalid release tag/, tag_name);
    assert.throws(() => createWebsiteReleaseData([release, valid]), /Invalid release tag/, tag_name);
  }
});

test("official DLC sample preserves Beta19 and Beta1 App release selection", async () => {
  const { readFile } = await import("node:fs/promises");
  const readJson = async (relative) => JSON.parse(await readFile(new URL(relative, import.meta.url), "utf8"));
  const catalog = await readJson("../../../modules/catalog.json");
  const sample = catalog.packages.find(item => item.id === "dropspace.sample");
  const sampleUrl = new URL(sample.url);
  const sampleTag = sampleUrl.pathname.split("/").filter(Boolean)[4];
  assert.equal(sampleTag, "dlc-dropspace.sample-1.0.0");
  const module = { tag_name: sampleTag, draft: false, prerelease: true,
    html_url: `https://github.com/airanluo-dot/DropSpace/releases/tag/${sampleTag}`,
    assets: [{ name: sampleUrl.pathname.split("/").at(-1), size: sample.bytes, browser_download_url: sample.url }] };
  const baseline = await readJson("../../../docs/dev/evidence/ten-language/public-download-verification.json");
  const beta19 = { tag_name: baseline.release, name: `DropSpace ${baseline.release}`,
    body: await readFile(new URL("../../../.github/release-notes/v0.3.1-beta.19.md", import.meta.url), "utf8"),
    draft: baseline.draft, prerelease: baseline.prerelease, published_at: baseline.publishedAt, html_url: baseline.releaseUrl,
    assets: baseline.assets.map(asset => ({ name: asset.name, size: asset.bytes, browser_download_url: asset.url })) };
  // Beta1 is an unpublished identity/body fixture; inherited sizes do not claim published Beta1 bytes.
  const beta1Tag = "v0.3.2-beta.1";
  const beta1 = { ...beta19, tag_name: beta1Tag, name: `DropSpace ${beta1Tag}`,
    html_url: `https://github.com/airanluo-dot/DropSpace/releases/tag/${beta1Tag}`,
    body: await readFile(new URL("../../../.github/release-notes/v0.3.2-beta.1.md", import.meta.url), "utf8"),
    assets: beta19.assets.map(asset => ({ ...asset,
      browser_download_url: `https://github.com/airanluo-dot/DropSpace/releases/download/${beta1Tag}/${asset.name}` })) };
  const committed = await readJson("../data/releases.json");
  const stableApi = committed.api.releases.find(item => !item.isPrerelease);
  const stable = { tag_name: stableApi.tagName, name: stableApi.name, body: stableApi.body, draft: false, prerelease: false,
    published_at: stableApi.publishedAt, html_url: stableApi.htmlUrl,
    assets: stableApi.assets.map(asset => ({ name: asset.name, size: asset.size, browser_download_url: asset.downloadUrl })) };
  const data = createWebsiteReleaseData([module, beta19, beta1, stable]);
  assert.equal(data.stable.tag, stable.tag_name);
  assert.equal(data.prereleases[0].tag, beta1Tag);
  assert.deepEqual(data.api.releases.map(item => item.tagName), [beta1Tag, baseline.release, stable.tag_name]);
  assert.equal(createLatestChangeApi(data.api).release.tagName, beta1Tag);
  assert.deepEqual(normalizeGitHubReleases([module, beta19, beta1, stable]).releases.map(item => item.tagName),
    [baseline.release, beta1Tag, stable.tag_name]);
});

test("rejects mismatched release, asset and schema identities", () => {
  assert.throws(() => normalizeGitHubReleases([{ ...valid, html_url: "https://attacker.invalid/release" }]));
  assert.throws(() => normalizeGitHubReleases([{ ...valid, assets: [{ ...valid.assets[0], browser_download_url: "https://attacker.invalid/update.exe" }] }]));
  assert.throws(() => validateReleaseApi({ schemaVersion: 2, releases: [] }));
});

test("latest-change contract follows the newest published release with a variable highlight count", () => {
  const body = `# DropSpace v0.2.1-preview.1 — Release-driven website\n\n## Highlights\n\n- API-driven headline\n- Variable summary list\n- Build snapshot fallback\n\n## 中文说明\n\n- 标题由接口更新\n- 摘要数量可变`;
  const preview = {
    ...valid,
    tag_name: "v0.2.1-preview.1",
    name: "DropSpace v0.2.1-preview.1",
    body,
    published_at: "2026-08-20T00:00:00Z",
    html_url: "https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.1-preview.1",
    assets: valid.assets.map((asset) => ({
      ...asset,
      browser_download_url: "https://github.com/airanluo-dot/DropSpace/releases/download/v0.2.1-preview.1/DropSpaceSetup.exe"
    }))
  };
  const payload = createLatestChangeApi(normalizeGitHubReleases([valid, preview], "2026-08-20T00:01:00Z"));
  assert.equal(validateLatestChangeApi(payload), payload);
  assert.equal(payload.release.tagName, "v0.2.1-preview.1");
  assert.equal(payload.release.headline["zh-CN"], "最新 Beta。");
  assert.equal(payload.release.title, "Release-driven website");
  assert.deepEqual(payload.release.highlights.en, ["API-driven headline", "Variable summary list", "Build snapshot fallback"]);
  assert.deepEqual(payload.release.highlights["zh-CN"], ["标题由接口更新", "摘要数量可变"]);
});

test("latest-change contract rejects unofficial identity and unbounded summaries", () => {
  const payload = createLatestChangeApi(normalizeGitHubReleases([valid]));
  assert.throws(() => validateLatestChangeApi({ ...payload, release: { ...payload.release, htmlUrl: "https://attacker.invalid/release" } }));
  assert.throws(() => validateLatestChangeApi({
    ...payload,
    release: { ...payload.release, highlights: { ...payload.release.highlights, en: Array(7).fill("Too many") } }
  }));
});

test("requires complete current Stable and Preview assets", () => {
  const assetNames = ["DropSpaceSetup.exe", "DropSpace.exe", "DropSpace-x64.msix", "SHA256SUMS.txt", "update-manifest.json"];
  const complete = (tag, prerelease) => ({
    ...valid,
    tag_name: tag,
    name: `DropSpace ${tag}`,
    prerelease,
    html_url: `https://github.com/airanluo-dot/DropSpace/releases/tag/${tag}`,
    assets: assetNames.map((name) => ({
      name,
      size: 42,
      browser_download_url: `https://github.com/airanluo-dot/DropSpace/releases/download/${tag}/${name}`
    }))
  });
  const stable = complete("v0.1.0", false);
  const preview = complete("v0.2.0-preview.5", true);
  assert.doesNotThrow(() => createWebsiteReleaseData([preview, stable]));
  assert.throws(() => createWebsiteReleaseData([{ ...preview, assets: preview.assets.slice(0, -1) }, stable]));
  assert.throws(() => createWebsiteReleaseData([preview, { ...stable, assets: stable.assets.slice(0, -1) }]));
  assert.throws(() => createWebsiteReleaseData([preview]));
  assert.throws(() => createWebsiteReleaseData([{ ...preview, html_url: "http://github.com/insecure" }, stable]));
});

test("keeps the current Stable release when the latest Preview window is full", () => {
  const assetNames = ["DropSpaceSetup.exe", "DropSpace.exe", "DropSpace-x64.msix", "SHA256SUMS.txt", "update-manifest.json"];
  const complete = (tag, prerelease) => ({
    ...valid,
    tag_name: tag,
    name: `DropSpace ${tag}`,
    prerelease,
    html_url: `https://github.com/airanluo-dot/DropSpace/releases/tag/${tag}`,
    assets: assetNames.map((name) => ({
      name,
      size: 42,
      browser_download_url: `https://github.com/airanluo-dot/DropSpace/releases/download/${tag}/${name}`
    }))
  });
  const prereleases = Array.from({ length: 21 }, (_, index) => complete("v0.3.0-preview." + (21 - index), true));
  const stable = complete("v0.2.1", false);
  const data = createWebsiteReleaseData([...prereleases, stable], "2026-09-09T00:00:00Z");
  assert.equal(data.stable.tag, "v0.2.1");
  assert.equal(data.api.releases.length, 20);
  assert.equal(data.prereleases[0].tag, "v0.3.0-preview.21");
  assert.equal(data.prereleases.length, 5);
});

test("Beta classification and numeric migration ordering preserve historical identities", async () => {
  const { compareReleaseTags } = await import("./release-contract.mjs");
  const tags = ["v0.3.0-preview.22", "v0.3.0-preview.23", "v0.3.0-beta.24", "v0.3.0-beta.25", "v0.3.0"];
  for (let i = 1; i < tags.length; i++) assert.ok(compareReleaseTags(tags[i - 1], tags[i]) < 0);
  for (const tag of tags.slice(0, -1)) {
    const release = { ...valid, tag_name: tag, name: `DropSpace ${tag}`, html_url: `https://github.com/airanluo-dot/DropSpace/releases/tag/${tag}`, assets: valid.assets.map(a => ({ ...a, browser_download_url: `https://github.com/airanluo-dot/DropSpace/releases/download/${tag}/${a.name}` })) };
    const api = normalizeGitHubReleases([release]);
    assert.equal(api.releases[0].tagName, tag);
    assert.equal(createLatestChangeApi(api).release.channel, "beta");
  }
});

test("Stable outranks every same-version prerelease and large components retain precision", async () => {
  const { compareReleaseTags } = await import("./release-contract.mjs");
  assert.ok(compareReleaseTags("v1.0.0", "v1.0.0-beta.10000") > 0);
  assert.ok(compareReleaseTags("v1.0.0", "v1.0.0-preview.9999") > 0);
  assert.ok(compareReleaseTags("v9007199254740993.0.0", "v9007199254740992.0.0") > 0);
});
